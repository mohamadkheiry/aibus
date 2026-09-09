import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

describe('ArkaCode distribution', () => {
  it('publishes direct Windows and Android downloads on the landing page', () => {
    expect(app).toContain('/downloads/ArkaCode-Windows-Setup.exe')
    expect(app).toContain('/downloads/ArkaCode-Android.apk')
    expect(app).toContain('id="arkacode"')
  })

  it('keeps the product section responsive on tablets and phones', () => {
    expect(styles).toContain('@media(max-width:1100px){.lp-arkacode')
    expect(styles).toContain('@media(max-width:760px){.lp-arkacode')
    expect(styles).toContain('.lp-download-grid{width:calc(100% - 20px);margin-top:-24px;grid-template-columns:1fr')
  })
})
