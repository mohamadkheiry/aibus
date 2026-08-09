import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

describe('role and profile administration', () => {
  it('connects live role changes to the administration API', () => {
    expect(app).toContain("`/api/admin/users/${user.id}/role`")
    expect(app).toContain("role:promoting?'SuperAdmin':'User'")
    expect(app).toContain('isPrimarySuperAdmin')
    expect(app).toContain('نقش مدیر اصلی قابل تغییر نیست')
  })

  it('lets the signed-in user edit an optional nickname', () => {
    expect(app).toContain('function ProfileEditor')
    expect(app).toContain("request<User>('/api/me',{method:'PUT'")
    expect(app).toContain('سلام {preview} 👋')
  })

  it('keeps role and profile controls responsive', () => {
    expect(styles).toContain('.role-policy-banner{')
    expect(styles).toContain('.role-change-card{')
    expect(styles).toContain('@media(max-width:760px){.app-shell .profile{display:flex')
  })
})
