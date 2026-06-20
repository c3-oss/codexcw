module.exports = {
  extends: ['@commitlint/config-conventional'],
  rules: {
    // Conventional Commits in this repo require an explicit scope, e.g.
    //   feat(cli): add `status` subcommand
    'scope-empty': [2, 'never']
  }
}
