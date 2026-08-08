import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import toast from 'react-hot-toast'
import {
  ArrowUp, Bot, Check, Copy, KeyRound, Plus,
  Search, ShieldCheck, Sparkles, Trash2, UserRound, Zap
} from 'lucide-react'
import { API, type Model, request, safeApiError, safeApiErrorFromText, type UserKey } from './api'
import { extractAssistantText, isChatCompatibleModel, keyAllowsModel } from './arkaChatUtils'

type ChatMessage = {
  id: string
  role: 'user' | 'assistant'
  content: string
  pending?: boolean
  error?: boolean
  modelName?: string
  latencyMs?: number
  totalTokens?: number
}

const makeId = () => globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`
const suggestions = [
  'برای رشد یک کسب‌وکار آنلاین سه ایده عملی پیشنهاد بده.',
  'این هفته را برای من به یک برنامه کاری متمرکز تبدیل کن.',
  'یک متن معرفی حرفه‌ای و کوتاه برای محصول هوش مصنوعی بنویس.',
  'تفاوت API و SDK را با یک مثال ساده توضیح بده.'
]

function ProviderMark({ model }: { model: Model }) {
  return <span className="arka-provider-mark" aria-hidden="true">
    <b>{model.provider.name.slice(0, 2).toUpperCase()}</b>
    {model.provider.logoUrl && <img src={model.provider.logoUrl} alt="" onError={event => { event.currentTarget.style.display = 'none' }} />}
  </span>
}

export function ArkaChat({ onOpenKeys }: { onOpenKeys: () => void }) {
  const [models, setModels] = useState<Model[]>([])
  const [keys, setKeys] = useState<UserKey[]>([])
  const [selectedModelId, setSelectedModelId] = useState(() => localStorage.getItem('arkachat_model') || '')
  const [selectedKeyId, setSelectedKeyId] = useState(() => localStorage.getItem('arkachat_key') || '')
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [prompt, setPrompt] = useState('')
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const messagesEndRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const abortRef = useRef<AbortController | null>(null)

  useEffect(() => {
    Promise.all([request<Model[]>('/api/models'), request<UserKey[]>('/api/keys')])
      .then(([modelData, keyData]) => {
        setModels(modelData.filter(isChatCompatibleModel).sort((a, b) => `${a.provider.name}${a.displayName}`.localeCompare(`${b.provider.name}${b.displayName}`)))
        setKeys(keyData)
        const usableKeys = keyData.filter(key => key.isActive && key.canReveal)
        const preferredKeyId = localStorage.getItem('arkachat_key') || ''
        setSelectedKeyId(usableKeys.some(key => key.id === preferredKeyId) ? preferredKeyId : usableKeys[0]?.id || '')
      })
      .catch(error => toast.error((error as Error).message))
      .finally(() => setLoading(false))
  }, [])

  const selectedKey = keys.find(key => key.id === selectedKeyId) || null
  const allowedModels = useMemo(
    () => selectedKey ? models.filter(model => keyAllowsModel(selectedKey, model.modelId)) : models,
    [models, selectedKey]
  )
  const visibleModels = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    if (!query) return allowedModels
    return allowedModels.filter(model => `${model.displayName} ${model.modelId} ${model.provider.name}`.toLocaleLowerCase().includes(query))
  }, [allowedModels, search])
  const selectedModel = allowedModels.find(model => model.modelId === selectedModelId) || null

  useEffect(() => {
    if (!allowedModels.length) {
      setSelectedModelId('')
      return
    }
    if (!allowedModels.some(model => model.modelId === selectedModelId)) {
      const saved = localStorage.getItem('arkachat_model')
      setSelectedModelId(allowedModels.find(model => model.modelId === saved)?.modelId || allowedModels[0].modelId)
    }
  }, [allowedModels, selectedModelId])
  useEffect(() => { if (selectedModelId) localStorage.setItem('arkachat_model', selectedModelId) }, [selectedModelId])
  useEffect(() => { if (selectedKeyId) localStorage.setItem('arkachat_key', selectedKeyId) }, [selectedKeyId])
  useEffect(() => { messagesEndRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }) }, [messages, busy])

  const clearChat = () => {
    abortRef.current?.abort()
    setBusy(false)
    setMessages([])
    setPrompt('')
    requestAnimationFrame(() => inputRef.current?.focus())
  }

  const send = async (suggestedPrompt?: string) => {
    const content = (suggestedPrompt ?? prompt).trim()
    if (!content || busy) return
    if (!selectedKey) return toast.error('ابتدا یک کلید فعال برای ArkaChat انتخاب کنید.')
    if (!selectedKey.canReveal) return toast.error('این کلید قدیمی است؛ ابتدا از بخش کلیدها آن را تعویض کنید.')
    if (!selectedModel) return toast.error('یک مدل سازگار با چت انتخاب کنید.')

    const userMessage: ChatMessage = { id: makeId(), role: 'user', content }
    const assistantId = makeId()
    const assistantMessage: ChatMessage = { id: assistantId, role: 'assistant', content: '', pending: true, modelName: selectedModel.displayName }
    const conversation = [...messages, userMessage]
    setMessages([...conversation, assistantMessage])
    setPrompt('')
    setBusy(true)
    const controller = new AbortController()
    abortRef.current = controller
    const started = performance.now()

    try {
      const revealed = await request<{ apiKey: string }>(`/api/keys/${selectedKey.id}/reveal`, { method: 'POST', signal: controller.signal })
      const usesResponsesApi = selectedModel.endpointPath.replace(/\/$/, '').endsWith('/responses')
      const endpoint = `${API || window.location.origin}${usesResponsesApi ? '/v1/responses' : '/v1/chat/completions'}`
      const requestMessages = conversation.slice(-30).map(message => ({ role: message.role, content: message.content }))
      const response = await fetch(endpoint, {
        method: 'POST',
        signal: controller.signal,
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${revealed.apiKey}` },
        body: JSON.stringify(usesResponsesApi
          ? { model: selectedModel.modelId, input: requestMessages, stream: false }
          : { model: selectedModel.modelId, messages: requestMessages, stream: false })
      })
      const responseText = await response.text()
      if (!response.ok) throw safeApiErrorFromText(responseText, response.status)
      let data: unknown
      try { data = JSON.parse(responseText) } catch { data = { content: responseText } }
      const answer = extractAssistantText(data)
      if (!answer) throw new Error('پاسخ مدل دریافت شد، اما متن قابل‌نمایشی در آن وجود نداشت.')
      const usage = data && typeof data === 'object' && 'usage' in data ? (data as { usage?: { total_tokens?: number } }).usage : undefined
      setMessages(current => current.map(message => message.id === assistantId ? {
        ...message,
        content: answer,
        pending: false,
        latencyMs: Math.round(performance.now() - started),
        totalTokens: usage?.total_tokens
      } : message))
    } catch (error) {
      const aborted = error instanceof DOMException && error.name === 'AbortError'
      const safe = aborted ? { message: 'تولید پاسخ متوقف شد.' } : (typeof error === 'object' && error && 'message' in error ? error as { message: string } : safeApiError(error))
      setMessages(current => current.map(message => message.id === assistantId ? { ...message, content: safe.message, pending: false, error: !aborted } : message))
      if (!aborted) toast.error(safe.message)
    } finally {
      abortRef.current = null
      setBusy(false)
      requestAnimationFrame(() => inputRef.current?.focus())
    }
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault()
      void send()
    }
  }

  if (loading) return <div className="arka-chat-loading"><Sparkles/><span>در حال آماده‌سازی ArkaChat...</span></div>

  return <section className="arka-chat-page" aria-label="ArkaChat">
    <header className="arka-chat-head">
      <div className="arka-chat-brand"><span><Sparkles/></span><div><small>گفت‌وگوی چندمدلی AiBus</small><h1>Arka<span>Chat</span></h1></div><em><i/>آنلاین</em></div>
      <div className="arka-chat-toolbar">
        <label className="arka-key-select"><span><KeyRound/>کلید مصرف</span><select value={selectedKeyId} onChange={event => setSelectedKeyId(event.target.value)} aria-label="انتخاب کلید ArkaChat"><option value="">انتخاب کلید</option>{keys.map(key => <option key={key.id} value={key.id} disabled={!key.isActive || !key.canReveal}>{key.name}{!key.isActive ? ' — غیرفعال' : !key.canReveal ? ' — نیازمند تعویض' : ''}</option>)}</select></label>
        <button className="arka-new-chat" onClick={clearChat}><Plus/>گفت‌وگوی جدید</button>
      </div>
    </header>

    <div className="arka-model-bar">
      <div className="arka-selected-model">{selectedModel ? <><ProviderMark model={selectedModel}/><span><small>مدل فعال</small><b>{selectedModel.displayName}</b><code dir="ltr">{selectedModel.modelId}</code></span><div className="arka-model-flags"><em><Zap/>{selectedModel.supportsStreaming ? 'Stream' : 'Standard'}</em><em><ShieldCheck/>AiBus</em></div></> : <><Bot/><span><b>مدلی در دسترس نیست</b><small>دسترسی کلید انتخاب‌شده را بررسی کنید.</small></span></>}</div>
      <div className="arka-model-picker"><Search/><input value={search} onChange={event => setSearch(event.target.value)} placeholder="جست‌وجوی مدل یا شرکت..." aria-label="جست‌وجوی مدل"/><select value={selectedModelId} onChange={event => setSelectedModelId(event.target.value)} aria-label="انتخاب مدل چت"><option value="">انتخاب مدل</option>{visibleModels.map(model => <option key={model.id} value={model.modelId}>{model.provider.name} · {model.displayName}</option>)}</select><span><Check/>{allowedModels.length} مدل قابل استفاده</span></div>
    </div>

    {!keys.some(key => key.isActive && key.canReveal) && <div className="arka-key-alert"><KeyRound/><div><b>برای شروع، یک کلید API فعال نیاز دارید.</b><p>ArkaChat از همان محدودیت‌ها و دسترسی مدل‌های کلید شما استفاده می‌کند.</p></div><button onClick={onOpenKeys}>مدیریت کلیدها</button></div>}

    <div className={`arka-conversation ${messages.length ? 'has-messages' : ''}`}>
      <div className="arka-messages" aria-live="polite">
        {!messages.length && <div className="arka-welcome"><div className="arka-welcome-orb"><Sparkles/><i/><i/><i/></div><span>آماده برای فکرکردن با شما</span><h2>امروز چه چیزی را با هم بسازیم؟</h2><p>از مدل دلخواهتان برای نوشتن، تحلیل، ایده‌پردازی و حل مسئله استفاده کنید.</p><div className="arka-suggestions">{suggestions.map((suggestion, index) => <button key={suggestion} onClick={() => void send(suggestion)} disabled={!selectedKey || !selectedModel}><i>{String(index + 1).padStart(2, '0')}</i><span>{suggestion}</span><ArrowUp/></button>)}</div></div>}
        {messages.map(message => <article key={message.id} className={`arka-message ${message.role} ${message.error ? 'error' : ''}`}><div className="arka-message-avatar">{message.role === 'assistant' ? <Sparkles/> : <UserRound/>}</div><div className="arka-message-body"><header><b>{message.role === 'assistant' ? message.modelName || 'ArkaChat' : 'شما'}</b>{message.role === 'assistant' && !message.pending && !message.error && <span>{message.latencyMs ? `${message.latencyMs}ms` : ''}{message.totalTokens ? ` · ${message.totalTokens} token` : ''}</span>}</header>{message.pending ? <div className="arka-thinking"><i/><i/><i/><span>در حال ساخت پاسخ...</span></div> : <p>{message.content}</p>}{message.role === 'assistant' && message.content && !message.pending && <footer><button onClick={() => { void navigator.clipboard.writeText(message.content); toast.success('پاسخ کپی شد') }}><Copy/>کپی پاسخ</button></footer>}</div></article>)}
        <div ref={messagesEndRef}/>
      </div>

      <div className="arka-composer-wrap"><div className="arka-composer"><textarea ref={inputRef} rows={1} value={prompt} maxLength={12000} onChange={event => setPrompt(event.target.value)} onKeyDown={handleKeyDown} placeholder={selectedModel ? `پیام به ${selectedModel.displayName}...` : 'ابتدا مدل و کلید را انتخاب کنید...'} disabled={!selectedKey || !selectedModel || busy} aria-label="پیام ArkaChat"/><div className="arka-composer-foot"><span><ShieldCheck/>درخواست امن از مسیر AiBus</span><small dir="ltr">{prompt.length.toLocaleString('en-US')} / 12,000</small>{busy ? <button className="stop" onClick={() => abortRef.current?.abort()} aria-label="توقف پاسخ"><span/></button> : <button className="send" onClick={() => void send()} disabled={!prompt.trim() || !selectedKey || !selectedModel} aria-label="ارسال پیام"><ArrowUp/></button>}</div></div><div className="arka-chat-note"><Sparkles/>پاسخ مدل‌های هوش مصنوعی ممکن است دقیق نباشد؛ اطلاعات مهم را بررسی کنید.<button onClick={clearChat}><Trash2/>پاک‌کردن گفتگو</button></div></div>
    </div>
  </section>
}
