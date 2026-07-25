import { describe, expect, it } from 'vitest'
import { createCodeRecipe, type CodeRecipeLanguage } from './App'

const endpoint = 'https://aibus.00f.ir/v1/chat/completions'
const payload = JSON.stringify({
  model: 'gpt-5-mini',
  messages: [{ role: 'user', content: 'سلام AiBus' }],
  stream: false
}, null, 2)

describe('playground code recipes', () => {
  it.each<CodeRecipeLanguage>(['curl', 'javascript', 'python', 'csharp', 'php', 'go'])(
    'builds a safe %s request from the editable JSON',
    language => {
      const recipe = createCodeRecipe(language, endpoint, payload)

      expect(recipe).toContain(endpoint)
      expect(recipe).toContain('YOUR_AIBUS_API_KEY')
      expect(recipe).toContain('gpt-5-mini')
      expect(recipe).toContain('Authorization')
    }
  )

  it('generates stream-aware JavaScript and Python examples', () => {
    expect(createCodeRecipe('javascript', endpoint, payload)).toContain('text/event-stream')
    expect(createCodeRecipe('python', endpoint, payload)).toContain("payload.get('stream')")
  })
})
