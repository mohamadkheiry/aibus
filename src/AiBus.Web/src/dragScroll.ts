export const DRAG_SCROLL_THRESHOLD = 10

export type DragIntent = 'pending' | 'horizontal' | 'vertical'

export function dragIntent(distanceX: number, distanceY: number): DragIntent {
  const horizontal = Math.abs(distanceX)
  const vertical = Math.abs(distanceY)
  if (Math.max(horizontal, vertical) < DRAG_SCROLL_THRESHOLD) return 'pending'
  return horizontal > vertical ? 'horizontal' : 'vertical'
}
