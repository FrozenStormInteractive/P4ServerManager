/**
 * Rules of the accounts of the web interface, the same as the ones of the daemon.
 */
export const PASSWORD_MIN_LENGTH = 10
export const PASSWORD_MAX_LENGTH = 128
export const USER_NAME_PATTERN = /^[A-Z0-9][\w.@-]{0,63}$/i

type Translate = (key: string, named?: Record<string, unknown>) => string
type Rule = (value: unknown) => true | string

export function usernameRules (t: Translate): Rule[] {
  return [
    value => !!value || t('auth.rules.required'),
    value => USER_NAME_PATTERN.test(String(value)) || t('auth.rules.userNameFormat'),
  ]
}

export function newPasswordRules (t: Translate): Rule[] {
  return [
    value => {
      const length = String(value ?? '').length
      return (length >= PASSWORD_MIN_LENGTH && length <= PASSWORD_MAX_LENGTH)
        || t('auth.rules.passwordLength', { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_LENGTH })
    },
  ]
}

/**
 * Checks that the confirmation repeats the password.
 */
export function confirmationRules (t: Translate, password: () => string): Rule[] {
  return [value => value === password() || t('auth.rules.passwordsDiffer')]
}
