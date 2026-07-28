import { describe, expect, it } from 'vitest'
import {
  canDeleteCredential,
  credentialBalancePayload,
  nextCredentialDeleteStep,
  validateCredentialBalanceDraft,
  type CredentialDeleteStep,
} from './credentialAdmin'

describe('provider credential balance form', () => {
  it('accepts non-negative decimal values and creates a numeric payload', () => {
    const draft = { initialBalanceUsd: '25.50', remainingBalanceUsd: '7.25', alertThresholdUsd: '2' }
    expect(validateCredentialBalanceDraft(draft)).toBeNull()
    expect(credentialBalancePayload(draft)).toEqual({
      initialBalanceUsd: 25.5,
      remainingBalanceUsd: 7.25,
      alertThresholdUsd: 2,
    })
  })

  it.each([
    { initialBalanceUsd: '-1', remainingBalanceUsd: '1', alertThresholdUsd: '1' },
    { initialBalanceUsd: '1', remainingBalanceUsd: '', alertThresholdUsd: '1' },
    { initialBalanceUsd: '1', remainingBalanceUsd: '1', alertThresholdUsd: 'not-a-number' },
    { initialBalanceUsd: '1000000001', remainingBalanceUsd: '1', alertThresholdUsd: '1' },
  ])('rejects invalid or negative amounts', draft => {
    expect(validateCredentialBalanceDraft(draft)).not.toBeNull()
  })

  it('rejects a remaining balance greater than the initial balance, including a zero initial balance', () => {
    expect(validateCredentialBalanceDraft({
      initialBalanceUsd: '0',
      remainingBalanceUsd: '0.01',
      alertThresholdUsd: '0',
    })).toBe('موجودی فعلی نمی‌تواند از موجودی اولیه بیشتر باشد.')

    expect(validateCredentialBalanceDraft({
      initialBalanceUsd: '10',
      remainingBalanceUsd: '10.000001',
      alertThresholdUsd: '1',
    })).toBe('موجودی فعلی نمی‌تواند از موجودی اولیه بیشتر باشد.')
  })
})

describe('provider credential deletion confirmation', () => {
  it('requires the warning step and the typed final confirmation', () => {
    let step: CredentialDeleteStep = 'idle'
    expect(canDeleteCredential(step, 'حذف')).toBe(false)

    step = nextCredentialDeleteStep(step)
    expect(step).toBe('warning')
    expect(canDeleteCredential(step, 'حذف')).toBe(false)

    step = nextCredentialDeleteStep(step)
    expect(step).toBe('verify')
    expect(canDeleteCredential(step, 'حذف')).toBe(true)
    expect(canDeleteCredential(step, 'حذ ف')).toBe(false)
  })
})
