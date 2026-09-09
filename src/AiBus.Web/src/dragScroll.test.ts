import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { dragIntent, DRAG_SCROLL_THRESHOLD } from './dragScroll'

describe('drag-scroll pointer intent', () => {
  it('keeps normal mouse jitter as a button click', () => {
    expect(DRAG_SCROLL_THRESHOLD).toBe(10)
    expect(dragIntent(6, 1)).toBe('pending')
    expect(dragIntent(-9, 2)).toBe('pending')
  })

  it('starts dragging only for a deliberate horizontal move', () => {
    expect(dragIntent(14, 3)).toBe('horizontal')
    expect(dragIntent(-18, 4)).toBe('horizontal')
  })

  it('leaves vertical gestures to page scrolling', () => {
    expect(dragIntent(3, 14)).toBe('vertical')
    expect(dragIntent(10, 10)).toBe('vertical')
  })

  it('captures the pointer only after horizontal drag intent is confirmed', () => {
    const app = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8')
    const pointerDown = app.slice(app.indexOf('const handlePointerDown'), app.indexOf('const handlePointerMove'))
    const pointerMove = app.slice(app.indexOf('const handlePointerMove'), app.indexOf('const preventDraggedClick'))
    expect(pointerDown).not.toContain('setPointerCapture')
    expect(pointerMove).toContain("if(intent==='vertical')")
    expect(pointerMove).toContain('setPointerCapture')
  })
})
