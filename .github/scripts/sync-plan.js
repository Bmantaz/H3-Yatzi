// Synkroniserer sprint-planen i .github/issues.json til GitHub:
// labels, milestones (med deadlines), issues (med ansvarlige) og lukning af gamle spikes.
// Kan køres igen og igen: eksisterende ting opdateres, intet oprettes dobbelt,
// og tekst I selv har skrevet i sprint-opgaverne bliver ikke overskrevet.

const fs = require('fs');

module.exports = async ({ github, context, core }) => {
  const plan = JSON.parse(fs.readFileSync('.github/issues.json', 'utf8'));
  const { owner, repo } = context.repo;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // ---------- Labels ----------
  const existingLabels = new Map(
    (await github.paginate(github.rest.issues.listLabelsForRepo, { owner, repo, per_page: 100 }))
      .map((l) => [l.name, l]));
  for (const l of plan.labels) {
    const cur = existingLabels.get(l.name);
    if (!cur) {
      await github.rest.issues.createLabel({ owner, repo, ...l });
      core.info(`Label oprettet: ${l.name}`);
    } else if (cur.color !== l.color || (cur.description || '') !== l.description) {
      await github.rest.issues.updateLabel({ owner, repo, name: l.name, color: l.color, description: l.description });
    }
  }

  // ---------- Milestones ----------
  const msList = await github.paginate(github.rest.issues.listMilestones, { owner, repo, state: 'all', per_page: 100 });
  const ms = Object.fromEntries(msList.map((m) => [m.title, m.number]));
  for (const m of plan.milestones) {
    const data = { title: m.title, description: m.description, ...(m.due_on ? { due_on: m.due_on } : {}) };
    if (ms[m.title]) {
      await github.rest.issues.updateMilestone({ owner, repo, milestone_number: ms[m.title], ...data });
    } else {
      const res = await github.rest.issues.createMilestone({ owner, repo, ...data });
      ms[m.title] = res.data.number;
      core.info(`Milestone oprettet: ${m.title}`);
    }
  }

  // ---------- Issues: find eksisterende ----------
  const all = (await github.paginate(github.rest.issues.listForRepo, { owner, repo, state: 'all', per_page: 100 }))
    .filter((i) => !i.pull_request);
  const byTitle = new Map(all.map((i) => [i.title, i]));
  const byKey = new Map();
  for (const i of all) {
    const m = (i.body || '').match(/<!-- key: (\S+) -->/);
    if (m) byKey.set(m[1], i);
  }

  // Pass 1: sørg for at alle issues findes
  const num = {};
  const created = new Set();
  for (const it of plan.issues) {
    let issue = byKey.get(it.key) || byTitle.get(it.title);
    if (!issue && it.managed !== 'create') {
      // Fald tilbage til titel-præfiks, fx "DoD 5:", hvis titlen er blevet ændret
      const prefix = it.title.split(':')[0] + ':';
      issue = all.find((i) => i.title.startsWith(prefix));
    }
    if (!issue) {
      if (it.managed !== 'create') { core.warning(`Mangler issue: ${it.title}`); continue; }
      const res = await github.rest.issues.create({
        owner, repo, title: it.title, body: it.body, labels: it.labels, milestone: ms[it.milestone],
      });
      issue = res.data;
      created.add(it.key);
      core.info(`Issue oprettet: #${issue.number} ${it.title}`);
      await sleep(800); // undgå secondary rate limit
    }
    num[it.key] = issue.number;
    it._issue = issue;
  }

  const render = (text) => text.replace(/\{\{(.+?)\}\}/g, (_, k) => (num[k] ? `#${num[k]}` : k));
  const nameOf = (person) => `${person} (@${plan.people[person]})`;

  // Pass 2: opdatér milestone, labels, ansvarlige og links
  for (const it of plan.issues) {
    const issue = it._issue;
    if (!issue) continue;
    const update = { owner, repo, issue_number: issue.number, milestone: ms[it.milestone] };

    const curLabels = issue.labels.map((l) => (typeof l === 'string' ? l : l.name));
    let labels = [...new Set([...curLabels, ...it.labels])];
    if (it.managed === 'existing') labels = labels.filter((l) => l !== 'spike');
    update.labels = labels;

    if (it.managed === 'create' && created.has(it.key)) {
      update.body = render(it.body).replace(/\*\*Ansvarlig:\*\* ([^\n(]+?)( \(med ([^)]+)\))?\n/, (_, main, __, extra) =>
        `**Ansvarlig:** ${nameOf(main.trim())}` +
        (extra ? ` (med ${extra.split(', ').map(nameOf).join(', ')})` : '') + '\n');
    }

    if (it.managed === 'epic') {
      const tasks = plan.issues.filter((t) => t.managed === 'create' && t.body.includes(`{{${it.key}}}`));
      const section = [
        '<!-- sprint-links:start -->',
        '## Sprint-opgaver',
        'Dette krav er opfyldt, når disse opgaver er lukket:',
        '',
        ...tasks.map((t) => `- [ ] #${num[t.key]}`),
        '<!-- sprint-links:end -->',
      ].join('\n');
      const body = issue.body || '';
      update.body = body.includes('<!-- sprint-links:start -->')
        ? body.replace(/<!-- sprint-links:start -->[\s\S]*<!-- sprint-links:end -->/, section)
        : `${body}\n\n${section}`;
    }

    await github.rest.issues.update(update);

    if (it.assignees) {
      const logins = it.assignees.map((p) => plan.people[p]).filter(Boolean);
      const missing = logins.filter((l) => !(issue.assignees || []).some((a) => a.login === l));
      if (missing.length) {
        try {
          await github.rest.issues.addAssignees({ owner, repo, issue_number: issue.number, assignees: missing });
        } catch (e) {
          core.warning(`Kunne ikke tildele ${missing.join(', ')} til #${issue.number} (har de accepteret invitationen?): ${e.message}`);
        }
      }
    }
  }

  // ---------- Luk gamle issues (spikes) ----------
  for (const c of plan.close || []) {
    const issue = byTitle.get(c.title);
    if (!issue || issue.state === 'closed') continue;
    await github.rest.issues.createComment({ owner, repo, issue_number: issue.number, body: render(c.comment) });
    await github.rest.issues.update({ owner, repo, issue_number: issue.number, state: 'closed', state_reason: 'completed' });
    core.info(`Lukket: #${issue.number} ${c.title}`);
  }

  // Tjek om tildelinger faktisk lykkedes (GitHub ignorerer stille ikke-medlemmer)
  for (const it of plan.issues.filter((i) => i.assignees && i._issue)) {
    const { data } = await github.rest.issues.get({ owner, repo, issue_number: it._issue.number });
    const have = data.assignees.map((a) => a.login);
    const lack = it.assignees.map((p) => plan.people[p]).filter((l) => !have.includes(l));
    if (lack.length) core.warning(`#${data.number} mangler ansvarlig: ${lack.join(', ')} – kør workflowet igen når invitationen er accepteret.`);
  }

  await core.summary
    .addHeading('Sprint-plan synkroniseret')
    .addRaw(`${created.size} nye issues oprettet, ${plan.issues.length} issues i alt i planen.`)
    .write();
};
