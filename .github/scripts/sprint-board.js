// Bygger issuet "📋 Sprint-board" ud fra milestones, issues og ansvarlige.
// Køres ved ændringer i issues, hver hverdag kl. 07:30, og manuelt.

module.exports = async ({ github, context, core }) => {
  const { owner, repo } = context.repo;
  const TITLE = '📋 Sprint-board';
  const OLD_TITLES = ['📋 DoD-status'];

  const all = (await github.paginate(github.rest.issues.listForRepo, { owner, repo, state: 'all', per_page: 100 }))
    .filter((i) => !i.pull_request);
  const milestones = await github.paginate(github.rest.issues.listMilestones, { owner, repo, state: 'all', per_page: 100 });

  const today = new Date().toLocaleDateString('sv-SE', { timeZone: 'Europe/Copenhagen' }); // YYYY-MM-DD
  const hasLabel = (i, name) => i.labels.some((l) => (typeof l === 'string' ? l : l.name) === name);
  const dueOf = (i) => ((i.body || '').match(/<!-- due: (\d{4}-\d{2}-\d{2}) -->/) || [])[1];
  const fmtDate = (d) => new Date(`${d}T12:00:00Z`).toLocaleDateString('da-DK', { weekday: 'short', day: 'numeric', month: 'numeric', timeZone: 'Europe/Copenhagen' });
  const name = (i) => `#${i.number} ${i.title.replace(/\|/g, '\\|')}`;
  const who = (i) => (i.assignees.length ? i.assignees.map((a) => `@${a.login}`).join(' ') : '_ingen_');

  const status = (i) => {
    if (i.state === 'closed') return '✅ Færdig';
    const due = dueOf(i);
    if (due && due < today) return '🔴 Forsinket';
    if (hasLabel(i, 'i-gang')) return '🟡 I gang';
    return '⚪ Ikke startet';
  };

  const bar = (done, total) => {
    if (!total) return '';
    const n = Math.round((done / total) * 10);
    return `${'🟩'.repeat(n)}${'⬜'.repeat(10 - n)} ${Math.round((done / total) * 100)}%`;
  };

  const sprints = milestones.filter((m) => m.title.startsWith('Sprint')).sort((a, b) => a.title.localeCompare(b.title));
  const out = [
    '_Opdateres automatisk af workflowet **Sprint-board**. Redigér ikke manuelt._',
    '',
    `Sidst opdateret: ${new Date().toLocaleString('da-DK', { timeZone: 'Europe/Copenhagen' })}`,
    '',
    'Status: ⚪ Ikke startet · 🟡 I gang (en PR linker til issuet) · 🔴 Forsinket · ✅ Færdig',
  ];

  // Aktuel sprint = første sprint med deadline i dag eller senere
  const current = sprints.find((m) => m.due_on && m.due_on.slice(0, 10) >= today) || sprints[sprints.length - 1];

  // Hvem laver hvad lige nu
  if (current) {
    const open = all.filter((i) => i.milestone && i.milestone.number === current.number && i.state === 'open');
    const people = new Map();
    for (const i of open) for (const a of i.assignees) {
      if (!people.has(a.login)) people.set(a.login, []);
      people.get(a.login).push(i);
    }
    out.push('', `## 👥 Hvem laver hvad – ${current.title}`, '');
    if (!people.size) out.push('_Alle opgaver i sprinten er færdige 🎉_');
    for (const [login, items] of [...people.entries()].sort()) {
      out.push(`- **@${login}:** ${items.sort((a, b) => (dueOf(a) || '').localeCompare(dueOf(b) || ''))
        .map((i) => `${status(i).split(' ')[0]} #${i.number}`).join(' · ')}`);
    }
  }

  for (const m of sprints) {
    const items = all.filter((i) => i.milestone && i.milestone.number === m.number)
      .sort((a, b) => a.title.localeCompare(b.title, 'da', { numeric: true }));
    const done = items.filter((i) => i.state === 'closed').length;
    const due = m.due_on ? m.due_on.slice(0, 10) : null;
    const late = due && due < today && done < items.length;
    out.push('', `## ${m === current ? '▶️ ' : ''}${m.title} – ${done}/${items.length}${late ? ' 🔴 deadline overskredet' : ''}`);
    if (m.description) out.push(`_${m.description}_`);
    out.push('', bar(done, items.length), '', '| Opgave | Ansvarlig | Deadline | Status |', '|---|---|---|---|');
    for (const i of items) {
      const d = dueOf(i);
      out.push(`| ${name(i)} | ${who(i)} | ${d ? fmtDate(d) : ''} | ${status(i)} |`);
    }
  }

  // DoD-overblik: et krav er opfyldt, når alle sprint-opgaver der "Bidrager til" det er lukket.
  // (Uafhængigt af om selve DoD-issuet er åbent eller lukket.)
  const sprintTasks = all.filter((i) => hasLabel(i, 'sprint-task'));
  const dod = all.filter((i) => hasLabel(i, 'dod')).sort((a, b) => a.number - b.number);
  const dodRows = dod.map((d) => {
    const re = new RegExp(`Bidrager til:\\*\\*[^\\n]*#${d.number}(?!\\d)`);
    const tasks = sprintTasks.filter((t) => re.test(t.body || ''));
    const closed = tasks.filter((t) => t.state === 'closed').length;
    return { d, tasks, closed, done: tasks.length > 0 && closed === tasks.length };
  });
  const dodDone = dodRows.filter((r) => r.done).length;
  out.push('', `## ✅ Definition of Done – ${dodDone}/${dod.length}`,
    '_Et krav er opfyldt, når alle sprint-opgaver der bidrager til det er færdige._', '',
    bar(dodDone, dod.length), '', '| Krav | Opgaver færdige | Status |', '|---|---|---|');
  for (const r of dodRows) {
    out.push(`| ${r.d.title.replace(/\|/g, '\\|')} | ${r.closed}/${r.tasks.length} | ${r.done ? '✅' : r.closed ? '🟡' : '⚪'} |`);
  }

  const body = out.join('\n');
  const tracker = all.find((i) => i.title === TITLE) || all.find((i) => OLD_TITLES.includes(i.title));
  if (tracker) {
    if (tracker.body !== body || tracker.title !== TITLE || tracker.state !== 'open') {
      await github.rest.issues.update({ owner, repo, issue_number: tracker.number, title: TITLE, body, state: 'open' });
    }
    if (!tracker.pinned) {
      try {
        // Fastgør issuet øverst i Issues (GraphQL)
        await github.graphql('mutation($id:ID!){ pinIssue(input:{issueId:$id}){ issue { number } } }', { id: tracker.node_id });
      } catch (e) { core.info(`Kunne ikke fastgøre issuet: ${e.message}`); }
    }
  } else {
    await github.rest.issues.create({ owner, repo, title: TITLE, body });
  }
};
