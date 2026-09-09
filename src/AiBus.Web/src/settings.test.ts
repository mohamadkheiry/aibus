import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

describe('central settings workspace', () => {
  it('implements all four interactive settings sections', () => {
    expect(app).toContain("type SettingsSection='finance'|'sms'|'payment'|'security'")
    expect(app).toContain("onClick={()=>setSection('finance')}")
    expect(app).toContain("onClick={()=>setSection('sms')}")
    expect(app).toContain("onClick={()=>setSection('payment')}")
    expect(app).toContain("onClick={()=>setSection('security')}")
  })

  it('connects configuration to save and SMS test endpoints', () => {
    expect(app).toContain("request('/api/admin/settings',{method:'PUT'")
    expect(app).toContain("request<{message:string}>('/api/admin/settings/test-sms'")
    expect(app).toContain('minimumTopUpUsd')
    expect(app).toContain('sessionLifetimeHours')
    expect(app).toContain('allowAdminImpersonation')
  })

  it('provides responsive status and control layouts', () => {
    expect(styles).toContain('.settings-statusbar{')
    expect(styles).toContain('.settings-four{grid-template-columns:repeat(4')
    expect(styles).toContain('@media(max-width:700px){.settings-statusbar')
    expect(styles).toContain('@media(max-width:430px){.settings-statusbar{grid-template-columns:1fr}')
  })
})
