module.exports = {
  extends: ["@commitlint/config-conventional"],
  rules: {
    "scope-empty": [2, "never"],
  },
  // Dependabot writes "<type>(deps): Bump ..." with a capitalized subject,
  // which conflicts with config-conventional's subject-case rule. Skip its
  // bot-generated bumps while keeping the convention strict for humans.
  ignores: [(message) => /^\S+\(deps(?:-dev)?\):\s+bump\s/i.test(message)],
};
