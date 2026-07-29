import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import importedStyles from './styles.css?raw'

// Vitest may stub CSS imports when CSS processing is disabled; the raw import is
// authoritative in Vite, while this fallback keeps the contract test executable.
const styles=importedStyles||readFileSync(new URL('./styles.css',import.meta.url),'utf8')

const compact = (value:string) => value.replace(/\s+/g,'').toLowerCase()
const rules = [...styles.replace(/\/\*[\s\S]*?\*\//g,'').matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(match=>({
  selectors:match[1].split(',').map(compact),
  declarations:compact(match[2])
}))

const declarationsFor = (selector:string) => rules
  .filter(rule=>rule.selectors.includes(compact(selector)))
  .map(rule=>rule.declarations)
  .join(';')

const expectDeclarations = (selector:string,...contracts:string[]) => {
  const declarations=declarationsFor(selector)
  expect(declarations,`missing responsive rule for ${selector}`).not.toBe('')
  contracts.forEach(contract=>expect(declarations,`${selector} must include ${contract}`).toContain(compact(contract)))
}

const mediaQueries=[...styles.matchAll(/@media\s*([^{}]+)\{/gi)].map(match=>compact(match[1]))
const hasMediaQuery=(...conditions:string[])=>mediaQueries.some(query=>conditions.every(condition=>query.includes(compact(condition))))

describe('mobile responsive CSS contracts',()=>{
  it('keeps the application shell and viewport-bound surfaces safe-area aware',()=>{
    expectDeclarations('.app-shell','min-height:100dvh')
    expectDeclarations('.app-shell .main','min-height:100dvh')
    expectDeclarations('.app-shell .sidebar','height:100dvh','safe-area-inset-top','safe-area-inset-right','safe-area-inset-bottom')
    expectDeclarations('.app-shell .topbar','safe-area-inset-top','safe-area-inset-right','safe-area-inset-left')
    expectDeclarations('.app-shell .content','safe-area-inset-right','safe-area-inset-left','safe-area-inset-bottom')
    expectDeclarations('.app-shell .modal-layer','safe-area-inset-top','safe-area-inset-right','safe-area-inset-left')
    expectDeclarations('.app-shell .modal','100dvh','safe-area-inset-top')
    expectDeclarations('.app-shell .modal-body','safe-area-inset-bottom')
  })

  it('defines the primary mobile breakpoint',()=>{
    expect(hasMediaQuery('max-width:760px')).toBe(true)
  })

  it('prevents mobile browsers from zooming form controls',()=>{
    expectDeclarations('.app-shell input:not([type=checkbox]):not([type=radio]):not([type=file])','font-size:16px')
    expectDeclarations('.app-shell textarea','font-size:16px')
    expectDeclarations('.app-shell select','font-size:16px')
  })

  it('provides minimum touch targets for icon and primary actions',()=>{
    expectDeclarations('.app-shell .icon-btn','width:44px','height:44px','min-width:44px','min-height:44px')
    expectDeclarations('.app-shell .primary-btn','min-height:44px')
  })

  it('preserves the mobile ticket master-detail contract',()=>{
    expectDeclarations('.ticket-mobile-back','display:none','display:inline-flex','min-width:44px','min-height:44px')
    expectDeclarations('.app-shell .ticket-workspace:not(.conversation-open) .ticket-conversation','display:none')
    expectDeclarations('.app-shell .ticket-workspace.conversation-open .ticket-list','display:none')
    expectDeclarations('.app-shell .ticket-workspace.conversation-open .ticket-conversation','display:flex')
  })

  it('includes a compact landscape layout',()=>{
    expect(hasMediaQuery('max-width:760px','orientation:landscape','max-height:500px')).toBe(true)
  })
})
