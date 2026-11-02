// Markerer issues som "i-gang", når en PR linker til dem med fx "Closes #12",
// og fjerner markeringen igen, når PR'en lukkes. Ved merge lukker GitHub selv issuet.

module.exports = async ({ github, context, core }) => {
  const { owner, repo } = context.repo;
  const pr = context.payload.pull_request;
  const LABEL = 'i-gang';

  const refs = [...(pr.body || '').matchAll(/\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)/gi)]
    .map((m) => Number(m[1]));
  const issues = [...new Set(refs)];

  if (context.payload.action === 'opened' && issues.length === 0) {
    await github.rest.issues.createComment({
      owner, repo, issue_number: pr.number,
      body: '👋 Denne PR linker ikke til et issue. Skriv fx `Closes #12` i beskrivelsen, så issuet vises som 🟡 I gang på sprint-boardet og lukkes automatisk ved merge.',
    });
    return;
  }

  for (const n of issues) {
    try {
      if (pr.state === 'open') {
        await github.rest.issues.addLabels({ owner, repo, issue_number: n, labels: [LABEL] });
        core.info(`#${n} markeret som i gang`);
      } else {
        await github.rest.issues.removeLabel({ owner, repo, issue_number: n, name: LABEL });
        core.info(`#${n}: i-gang fjernet`);
      }
    } catch (e) {
      core.info(`#${n}: ${e.message}`);
    }
  }
};
