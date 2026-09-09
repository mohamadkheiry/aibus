import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

describe('user request logs', () => {
  const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
  const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

  it('adds an authenticated user log page with scoped filters and privacy messaging', () => {
    expect(app).toContain("'my-logs':'لاگ درخواست‌های من'")
    expect(app).toContain("request<UserRequestLogs>(`/api/logs?${query}`)")
    expect(app).toContain('Body ذخیره نمی‌شود')
    expect(app).toContain('apiKeyId')
    expect(app).toContain('Trace ID کپی شد')
  })

  it('renders request logs as responsive cards on mobile', () => {
    expect(styles).toContain('.user-log-table td:before')
    expect(styles).toContain('content:attr(data-label)')
    expect(styles).toContain('.user-log-pagination')
  })
})
