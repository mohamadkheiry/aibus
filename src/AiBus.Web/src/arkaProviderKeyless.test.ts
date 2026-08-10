import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

describe('ARKA keyless provider administration', () => {
  const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
  const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

  it('keeps ARKA in providers while hiding upstream key management', () => {
    expect(app).toContain('requiresApiKey:boolean')
    expect(app).toContain("selected.requiresApiKey&&<button")
    expect(app).toContain('ARKA به API Key مبدا نیاز ندارد')
    expect(app).toContain('بدون نیاز به API Key مبدا')
  })

  it('explains the keyless route in the ARKA service editor', () => {
    expect(app).toContain('مسیر upstream بدون API Key مبدا در دسترس است')
    expect(styles).toContain('.arka-keyless-panel')
    expect(styles).toContain('.keyless-provider')
  })
})
