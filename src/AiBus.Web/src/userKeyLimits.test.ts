import { describe, expect, it } from 'vitest'
import {
  limitUsageState,
  userKeyLimitPayload,
  validateUserKeyLimitDraft,
  type UserKeyLimitDraft,
} from './userKeyLimits'

describe('user API key limit editor', () => {
  it('maps unlimited toggles to null and ignores their inactive inputs', () => {
    const draft:UserKeyLimitDraft={unlimitedRequests:true,requestLimit:'invalid',unlimitedSpend:true,spendLimitUsd:'-5'}
    expect(validateUserKeyLimitDraft(draft)).toBeNull()
    expect(userKeyLimitPayload(draft)).toEqual({requestLimit:null,spendLimitUsd:null})
  })

  it('accepts zero, integer request limits and decimal dollar limits', () => {
    const draft:UserKeyLimitDraft={unlimitedRequests:false,requestLimit:'0',unlimitedSpend:false,spendLimitUsd:'12.345678'}
    expect(validateUserKeyLimitDraft(draft)).toBeNull()
    expect(userKeyLimitPayload(draft)).toEqual({requestLimit:0,spendLimitUsd:12.345678})
  })

  it.each([
    [{unlimitedRequests:false,requestLimit:'',unlimitedSpend:true,spendLimitUsd:''},'سقف تعداد درخواست'],
    [{unlimitedRequests:false,requestLimit:'1.5',unlimitedSpend:true,spendLimitUsd:''},'سقف تعداد درخواست'],
    [{unlimitedRequests:false,requestLimit:'1000000001',unlimitedSpend:true,spendLimitUsd:''},'سقف تعداد درخواست'],
    [{unlimitedRequests:true,requestLimit:'',unlimitedSpend:false,spendLimitUsd:'-0.01'},'آستانه توقف هزینه'],
    [{unlimitedRequests:true,requestLimit:'',unlimitedSpend:false,spendLimitUsd:'1000000001'},'آستانه توقف هزینه'],
  ] as Array<[UserKeyLimitDraft,string]>)('rejects an invalid active limit', (draft,message) => {
    expect(validateUserKeyLimitDraft(draft)).toContain(message)
  })

  it('detects a newly reached cap, including an explicit zero cap', () => {
    expect(limitUsageState(0,null)).toBe('unlimited')
    expect(limitUsageState(4,5)).toBe('available')
    expect(limitUsageState(5,5)).toBe('reached')
    expect(limitUsageState(0,0)).toBe('reached')
  })
})
