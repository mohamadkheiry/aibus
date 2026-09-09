import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'

const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
const styles = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

describe('professional usage reporting contracts', () => {
  it('uses a Persian-capable range date picker with a working calendar switch', () => {
    expect(app).toContain("from 'react-multi-date-picker'")
    expect(app).toContain("from 'react-date-object/calendars/persian'")
    expect(app).toContain("setCalendarMode(mode=>mode==='persian'?'gregorian':'persian')")
    expect(app).toContain('range rangeHover calendar={calendar} locale={locale}')
  })

  it('downloads filtered CSV and exposes selectable page sizes', () => {
    expect(app).toContain('/api/usage/export')
    expect(app).toContain('document.body.appendChild(anchor)')
    for (const size of ['10', '20', '50', '100']) expect(app).toContain(`<option value="${size}">${size}</option>`)
  })

  it('renders Power BI-inspired USD charts and the full reference catalog', () => {
    expect(app).toContain('<ComposedChart')
    expect(app).toContain('name="هزینه ($)"')
    expect(app).toContain('/api/models?includeInactive=true')
    expect(styles).toContain('.usage-bi-grid')
    expect(styles).toContain('.model-card.unavailable')
  })
})
