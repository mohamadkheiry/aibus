import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
const chat = readFileSync(new URL('./ArkaChat.tsx', import.meta.url), 'utf8')
const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

describe('independent ArkaChat portal contracts', () => {
  it('routes ArkaChat outside the dashboard shell and uses the OTP login', () => {
    expect(app).toContain("location.pathname.toLowerCase().startsWith('/arkachat')")
    expect(app).toContain("if(portal==='arkachat')")
    expect(app).toContain("arkaChat={portal==='arkachat'}")
    expect(app).not.toMatch(/id:'chat',label:'ArkaChat'/)
  })

  it('provides conversation history, model selection and a private automatic user key', () => {
    expect(chat).toContain('arkachat_threads_')
    expect(chat).toContain('aria-label="انتخاب مدل چت"')
    expect(chat).toContain("name: 'ArkaChat'")
    expect(chat).not.toContain('arka-key-select')
  })

  it('has a responsive full-height standalone shell', () => {
    expect(styles).toContain('.arkachat-shell{height:100dvh')
    expect(styles).toContain('@media(max-width:900px)')
    expect(styles).toContain('.arkachat-mobile-head')
  })
})
