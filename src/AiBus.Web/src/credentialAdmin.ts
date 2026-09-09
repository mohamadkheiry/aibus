export type CredentialBalanceDraft = {
  initialBalanceUsd: string
  remainingBalanceUsd: string
  alertThresholdUsd: string
}

export type CredentialBalancePayload = {
  initialBalanceUsd: number
  remainingBalanceUsd: number
  alertThresholdUsd: number
}

export type CredentialDeleteStep = 'idle' | 'warning' | 'verify'

export const MAX_CREDENTIAL_BALANCE_USD = 1_000_000_000

const balanceFields: Array<[keyof CredentialBalanceDraft, string]> = [
  ['initialBalanceUsd', 'موجودی اولیه'],
  ['remainingBalanceUsd', 'موجودی فعلی'],
  ['alertThresholdUsd', 'آستانه هشدار'],
]

export function validateCredentialBalanceDraft(draft: CredentialBalanceDraft): string | null {
  for (const [field, label] of balanceFields) {
    const raw = draft[field].trim()
    const value = Number(raw)
    if (!raw || !Number.isFinite(value) || value < 0 || value > MAX_CREDENTIAL_BALANCE_USD) {
      return `${label} باید عددی بین صفر تا ${MAX_CREDENTIAL_BALANCE_USD.toLocaleString('en-US')} دلار باشد.`
    }
  }
  if (Number(draft.remainingBalanceUsd) > Number(draft.initialBalanceUsd)) {
    return 'موجودی فعلی نمی‌تواند از موجودی اولیه بیشتر باشد.'
  }
  return null
}

export function credentialBalancePayload(draft: CredentialBalanceDraft): CredentialBalancePayload {
  return {
    initialBalanceUsd: Number(draft.initialBalanceUsd),
    remainingBalanceUsd: Number(draft.remainingBalanceUsd),
    alertThresholdUsd: Number(draft.alertThresholdUsd),
  }
}

export function nextCredentialDeleteStep(step: CredentialDeleteStep): CredentialDeleteStep {
  if (step === 'idle') return 'warning'
  if (step === 'warning') return 'verify'
  return 'verify'
}

export function canDeleteCredential(step: CredentialDeleteStep, confirmation: string): boolean {
  return step === 'verify' && confirmation.trim() === 'حذف'
}
