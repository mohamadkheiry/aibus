import { describe, expect, it } from 'vitest'
import { safeApiError, safeApiErrorFromText } from './api'

describe('safeApiError', () => {
  it('preserves a Persian message emitted by the AiBus gateway', () => {
    const result=safeApiError({error:{code:'provider_quota_exhausted',message:'اعتبار سرویس OpenAI کافی نیست؛ هزینه‌ای کسر نشد.'}},503)

    expect(result).toEqual({
      code:'provider_quota_exhausted',
      message:'اعتبار سرویس OpenAI کافی نیست؛ هزینه‌ای کسر نشد.',
      status:503
    })
  })

  it('maps the raw OpenAI insufficient quota response without leaking it', () => {
    const raw=JSON.stringify({error:{type:'insufficient_quota',code:'insufficient_quota',message:'You exceeded your current quota, please check your plan and billing details. https://platform.openai.com/docs/guides/error-codes/api-errors'}})
    const result=safeApiErrorFromText(raw,429)

    expect(result.code).toBe('provider_quota_exhausted')
    expect(result.message).toContain('اعتبار سرویس هوش مصنوعی')
    expect(result.message).toContain('هزینه‌ای از کیف پول شما کسر نشد')
    expect(result.message).not.toContain('You exceeded')
    expect(result.message).not.toContain('openai.com')
  })

  it('keeps rate limiting distinct from exhausted provider credit', () => {
    const result=safeApiError({error:{code:'rate_limit_exceeded',message:'Rate limit reached for requests per minute.'}},429)

    expect(result.code).toBe('provider_rate_limited')
    expect(result.message).toContain('درخواست‌های زیادی')
    expect(result.message).not.toContain('Rate limit')
  })

  it('treats a generic request quota as rate limiting rather than financial credit exhaustion', () => {
    const result=safeApiError({error:{code:'RESOURCE_EXHAUSTED',message:'You exceeded your current quota for requests per minute.'}},429)

    expect(result.code).toBe('provider_rate_limited')
    expect(result.message).toContain('درخواست‌های زیادی')
  })

  it('never returns an unknown upstream body to the user', () => {
    const raw='Vendor internal failure: cluster=prod-secret; trace=https://vendor.example/private/42'
    const result=safeApiErrorFromText(raw,502)

    expect(result.code).toBe('provider_unavailable')
    expect(result.message).not.toContain('Vendor')
    expect(result.message).not.toContain('vendor.example')
    expect(result.message).toContain('موقتاً در دسترس نیست')
  })
})
