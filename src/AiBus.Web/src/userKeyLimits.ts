export const MAX_USER_KEY_LIMIT = 1_000_000_000

export type UserKeyLimitDraft = {
  unlimitedRequests: boolean
  requestLimit: string
  unlimitedSpend: boolean
  spendLimitUsd: string
}

export type UserKeyLimitPayload = {
  requestLimit: number | null
  spendLimitUsd: number | null
}

export type LimitUsageState = 'unlimited' | 'available' | 'reached'

export function validateUserKeyLimitDraft(draft: UserKeyLimitDraft): string | null {
  if (!draft.unlimitedRequests) {
    const raw = draft.requestLimit.trim()
    const value = Number(raw)
    if (!raw || !Number.isFinite(value) || !Number.isInteger(value) || value < 0 || value > MAX_USER_KEY_LIMIT) {
      return `سقف تعداد درخواست باید یک عدد صحیح بین صفر تا ${MAX_USER_KEY_LIMIT.toLocaleString('en-US')} باشد.`
    }
  }

  if (!draft.unlimitedSpend) {
    const raw = draft.spendLimitUsd.trim()
    const value = Number(raw)
    if (!raw || !Number.isFinite(value) || value < 0 || value > MAX_USER_KEY_LIMIT) {
      return `آستانه توقف هزینه باید عددی بین صفر تا ${MAX_USER_KEY_LIMIT.toLocaleString('en-US')} دلار باشد.`
    }
  }

  return null
}

export function userKeyLimitPayload(draft: UserKeyLimitDraft): UserKeyLimitPayload {
  return {
    requestLimit: draft.unlimitedRequests ? null : Number(draft.requestLimit),
    spendLimitUsd: draft.unlimitedSpend ? null : Number(draft.spendLimitUsd),
  }
}

export function limitUsageState(used: number, limit: number | null): LimitUsageState {
  if (limit === null) return 'unlimited'
  return used >= limit ? 'reached' : 'available'
}
