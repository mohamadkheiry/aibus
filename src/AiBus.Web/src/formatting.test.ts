import { describe, expect, it } from 'vitest'
import { usd } from './api'

describe('USD formatting', () => {
  it('always uses Latin digits and a dot decimal separator', () => {
    expect(usd(1234.5)).toBe('1,234.50')
    expect(usd(0.000035,6)).toBe('0.000035')
    expect(usd(98.76)).not.toMatch(/[۰-۹٠-٩]/)
  })
})
