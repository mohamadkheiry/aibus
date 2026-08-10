import { describe, expect, it } from 'vitest'
import { globalSearchMatches, normalizeGlobalSearch, rankGlobalSearch } from './globalSearch'

describe('global dashboard search',()=>{
  it('normalizes Persian and Arabic letter variants',()=>{
    expect(normalizeGlobalSearch('  كليد\u200cهاي API  ')).toBe('کلید های api')
    expect(globalSearchMatches('کلیدهای API و دسترسی مدل','كليد API')).toBe(true)
  })

  it('matches every word regardless of its position',()=>{
    expect(globalSearchMatches('گزارش مصرف و هزینه دلاری','هزینه گزارش')).toBe(true)
    expect(globalSearchMatches('گزارش مصرف','کاربر')).toBe(false)
  })

  it('ranks exact and prefix matches before partial matches',()=>{
    const results=rankGlobalSearch([
      {id:'partial',searchable:'مدیریت مدل‌ها'},
      {id:'exact',searchable:'مدل'},
      {id:'prefix',searchable:'مدل‌ها و آزمایشگاه'},
    ],'مدل')
    expect(results.map(result=>result.id)).toEqual(['exact','prefix','partial'])
  })
})
