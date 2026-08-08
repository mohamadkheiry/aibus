import { describe, expect, it } from 'vitest'
import type { Model, UserKey } from './api'
import { extractAssistantText, isChatCompatibleModel, keyAllowsModel } from './arkaChatUtils'

const key = (accessMode: string, modelRules: string[] = []): UserKey => ({
  id: 'key-1', name: 'ArkaChat', keyPrefix: 'aibus_', canReveal: true, isActive: true,
  requestCount: 0, spentUsd: 0, accessMode, modelRules, createdAtUtc: new Date().toISOString()
})

const model = (endpointPath = '/v1/chat/completions'): Model => ({
  id: 'model-1', providerId: 'provider-1', modelId: 'gpt-test', displayName: 'GPT Test', modality: 'text',
  serviceType: 'chat', endpointPath, region: 'global', isPreview: false, pricingComponents: [],
  inputPricePerMillionUsd: 1, outputPricePerMillionUsd: 2, contextWindow: 1000,
  supportsStreaming: true, supportsWebSocket: false, priceSyncedAtUtc: new Date().toISOString(),
  pricingSourceUrl: '', provider: { id: 'provider-1', name: 'OpenAI', slug: 'openai', logoUrl: '' }
})

describe('ArkaChat helpers', () => {
  it('honors all user-key model access modes', () => {
    expect(keyAllowsModel(key('all'), 'gpt-test')).toBe(true)
    expect(keyAllowsModel(key('allow', ['gpt-test']), 'gpt-test')).toBe(true)
    expect(keyAllowsModel(key('allow', ['other']), 'gpt-test')).toBe(false)
    expect(keyAllowsModel(key('deny', ['gpt-test']), 'gpt-test')).toBe(false)
  })

  it('accepts both chat-completions and Responses chat models', () => {
    expect(isChatCompatibleModel(model())).toBe(true)
    expect(isChatCompatibleModel(model('/v1/responses'))).toBe(true)
    const embedding = { ...model('/v1/embeddings'), serviceType: 'embeddings' }
    expect(isChatCompatibleModel(embedding)).toBe(false)
  })

  it('extracts OpenAI-compatible and responses-style text', () => {
    expect(extractAssistantText({ choices: [{ message: { content: 'سلام دنیا' } }] })).toBe('سلام دنیا')
    expect(extractAssistantText({ choices: [{ message: { content: [{ type: 'text', text: 'پاسخ چندبخشی' }] } }] })).toBe('پاسخ چندبخشی')
    expect(extractAssistantText({ output: [{ content: [{ type: 'output_text', text: 'پاسخ Responses' }] }] })).toBe('پاسخ Responses')
  })
})
