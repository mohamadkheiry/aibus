import type { Model, UserKey } from './api'

type UnknownRecord = Record<string, unknown>

const recordOf = (value: unknown): UnknownRecord | null =>
  value !== null && typeof value === 'object' && !Array.isArray(value) ? value as UnknownRecord : null

const textFromContent = (value: unknown): string => {
  if (typeof value === 'string') return value
  if (!Array.isArray(value)) return ''
  return value.map(item => {
    if (typeof item === 'string') return item
    const record = recordOf(item)
    return typeof record?.text === 'string'
      ? record.text
      : typeof record?.content === 'string'
        ? record.content
        : ''
  }).filter(Boolean).join('\n')
}

export const isChatCompatibleModel = (model: Model) => {
  const endpoint = model.endpointPath.replace(/\/$/, '')
  return model.serviceType === 'chat' && (endpoint.endsWith('/chat/completions') || endpoint.endsWith('/responses'))
}

export const keyAllowsModel = (key: UserKey, modelId: string) => {
  if (!key.isActive) return false
  if (key.accessMode === 'allow') return key.modelRules.includes(modelId)
  if (key.accessMode === 'deny') return !key.modelRules.includes(modelId)
  return true
}

export const preferredArkaChatKey = (keys: UserKey[]) =>
  keys.find(key => key.name.trim().toLocaleLowerCase() === 'arkachat' && key.isActive && key.canReveal)
  || keys.find(key => key.isActive && key.canReveal)
  || null

export function extractAssistantText(value: unknown): string {
  const body = recordOf(value)
  if (!body) return ''

  const choices = Array.isArray(body.choices) ? body.choices : []
  const choice = recordOf(choices[0])
  const message = recordOf(choice?.message)
  const delta = recordOf(choice?.delta)
  const choiceText = textFromContent(message?.content) || textFromContent(delta?.content) || textFromContent(choice?.text)
  if (choiceText) return choiceText

  if (typeof body.output_text === 'string') return body.output_text
  const directContent = textFromContent(body.content)
  if (directContent) return directContent

  if (Array.isArray(body.output)) {
    const outputText = body.output.flatMap(item => {
      const outputItem = recordOf(item)
      if (!outputItem) return []
      if (typeof outputItem.text === 'string') return [outputItem.text]
      if (!Array.isArray(outputItem.content)) return []
      return outputItem.content.map(part => {
        const contentPart = recordOf(part)
        return typeof contentPart?.text === 'string' ? contentPart.text : ''
      }).filter(Boolean)
    }).join('\n')
    if (outputText) return outputText
  }

  return ''
}
