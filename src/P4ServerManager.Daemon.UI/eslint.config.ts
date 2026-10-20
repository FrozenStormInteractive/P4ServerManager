import vuetify from 'eslint-config-vuetify'
import pluginOxlint from 'eslint-plugin-oxlint'

// eslint-config-vuetify already sets up Vue (vue-eslint-parser + eslint-plugin-vue), TypeScript
// (typescript-eslint, scripts in .vue files limited to `lang="ts"`) and ignores what .gitignore lists.
// The oxlint config goes last so it turns off the ESLint rules that oxlint already checks.
export default vuetify(
  {
    ts: true,
  },
  pluginOxlint.buildFromOxlintConfigFile('.oxlintrc.json'),
)
