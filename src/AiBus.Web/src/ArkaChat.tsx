import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import toast from 'react-hot-toast'
import {
  ArrowUp, Bot, Check, Copy, LayoutDashboard, LogOut, Menu,
  MessageSquareText, Plus, Search, ShieldCheck, Sparkles, Trash2, UserRound,
  Wallet, X, Zap
} from 'lucide-react'
import { API, type Model, request, safeApiError, safeApiErrorFromText, type User, type UserKey, usd } from './api'
import { extractAssistantText, isChatCompatibleModel, keyAllowsModel, preferredArkaChatKey } from './arkaChatUtils'

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

type ChatThread = {
  id: string
  title: string
  modelId: string
  messages: ChatMessage[]
  updatedAt: number
}

type ChatState = { threads: ChatThread[]; activeId: string }
type ChatKey = { raw: string; profile: UserKey }

const makeId = () => globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`
const suggestions = [
  'برای رشد یک کسب‌وکار آنلاین سه ایده عملی پیشنهاد بده.',
  'این هفته را برای من به یک برنامه کاری متمرکز تبدیل کن.',
  'یک متن معرفی حرفه‌ای و کوتاه برای محصول هوش مصنوعی بنویس.',
  'تفاوت API و SDK را با یک مثال ساده توضیح بده.'
]

const newThread = (modelId = ''): ChatThread => ({
  id: makeId(), title: 'گفت‌وگوی جدید', modelId, messages: [], updatedAt: Date.now()
})

function loadChatState(userId: string): ChatState {
  try {
    const stored = JSON.parse(localStorage.getItem(`arkachat_threads_${userId}`) || '[]') as ChatThread[]
    const threads = Array.isArray(stored)
      ? stored.filter(thread => thread && typeof thread.id === 'string' && Array.isArray(thread.messages)).slice(0, 50)
      : []
    if (threads.length) return { threads, activeId: threads[0].id }
  } catch { /* A malformed local draft should never block login. */ }
  const first = newThread(localStorage.getItem('arkachat_model') || '')
  return { threads: [first], activeId: first.id }
}

function ProviderMark({ model }: { model: Model }) {
  return <span className="arka-provider-mark" aria-hidden="true">
    <b>{model.provider.name.slice(0, 2).toUpperCase()}</b>
    {model.provider.logoUrl && <img src={model.provider.logoUrl} alt="" onError={event => { event.currentTarget.style.display = 'none' }} />}
  </span>
}

async function prepareChatKey(keys: UserKey[]): Promise<ChatKey> {
  const preferred = preferredArkaChatKey(keys)
  if (preferred) {
    const revealed = await request<{ apiKey: string }>(`/api/keys/${preferred.id}/reveal`, { method: 'POST' })
    return { raw: revealed.apiKey, profile: preferred }
  }

  const created = await request<{ id: string; apiKey: string; keyPrefix: string }>('/api/keys', {
    method: 'POST',
    body: JSON.stringify({ name: 'ArkaChat', requestLimit: null, spendLimitUsd: null, accessMode: 'all', modelRules: [] })
  })
  return {
    raw: created.apiKey,
    profile: {
      id: created.id, name: 'ArkaChat', keyPrefix: created.keyPrefix, canReveal: true, isActive: true,
      requestCount: 0, spentUsd: 0, accessMode: 'all', modelRules: [], createdAtUtc: new Date().toISOString()
    }
  }
}

export function ArkaChat({ user, onOpenDashboard, onLogout }: { user: User; onOpenDashboard: () => void; onLogout: () => void }) {
  const [models, setModels] = useState<Model[]>([])
  const [chatKey, setChatKey] = useState<ChatKey | null>(null)
  const [selectedModelId, setSelectedModelId] = useState(() => localStorage.getItem('arkachat_model') || '')
  const [chatState, setChatState] = useState<ChatState>(() => loadChatState(user.id))
  const [prompt, setPrompt] = useState('')
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const messagesEndRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const abortRef = useRef<AbortController | null>(null)

  useEffect(() => {
    let cancelled = false
    Promise.all([request<Model[]>('/api/models'), request<UserKey[]>('/api/keys')])
      .then(async ([modelData, keyData]) => {
        const compatible = modelData.filter(isChatCompatibleModel)
          .sort((a, b) => `${a.provider.name}${a.displayName}`.localeCompare(`${b.provider.name}${b.displayName}`))
        const prepared = await prepareChatKey(keyData)
        if (cancelled) return
        setModels(compatible)
        setChatKey(prepared)
      })
      .catch(error => toast.error((error as Error).message))
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [])

  const activeThread = chatState.threads.find(thread => thread.id === chatState.activeId) || chatState.threads[0]
  const messages = useMemo(() => activeThread?.messages || [], [activeThread])
  const allowedModels = useMemo(
    () => chatKey ? models.filter(model => keyAllowsModel(chatKey.profile, model.modelId)) : models,
    [models, chatKey]
  )
  const visibleModels = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    if (!query) return allowedModels
    return allowedModels.filter(model => `${model.displayName} ${model.modelId} ${model.provider.name}`.toLocaleLowerCase().includes(query))
  }, [allowedModels, search])
  const selectedModel = allowedModels.find(model => model.modelId === selectedModelId) || null

  useEffect(() => {
    if (!allowedModels.length) return
    const threadModel = activeThread?.modelId
    const next = allowedModels.find(model => model.modelId === threadModel)?.modelId
      || allowedModels.find(model => model.modelId === selectedModelId)?.modelId
      || allowedModels[0].modelId
    if (next !== selectedModelId) setSelectedModelId(next)
  }, [allowedModels, activeThread?.id, activeThread?.modelId, selectedModelId])

  useEffect(() => {
    if (!selectedModelId) return
    localStorage.setItem('arkachat_model', selectedModelId)
    setChatState(current => ({
      ...current,
      threads: current.threads.map(thread => thread.id === current.activeId ? { ...thread, modelId: selectedModelId } : thread)
    }))
  }, [selectedModelId])

  useEffect(() => {
    const persistent = chatState.threads.map(thread => ({
      ...thread,
      messages: thread.messages.filter(message => !message.pending)
    }))
    localStorage.setItem(`arkachat_threads_${user.id}`, JSON.stringify(persistent.slice(0, 50)))
  }, [chatState.threads, user.id])

  useEffect(() => { messagesEndRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }) }, [messages, busy])
  useEffect(() => {
    if (!sidebarOpen) return
    const close = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setSidebarOpen(false) }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [sidebarOpen])

  const updateThreadMessages = (threadId: string, updater: (messages: ChatMessage[]) => ChatMessage[]) => {
    setChatState(current => ({
      ...current,
      threads: current.threads.map(thread => thread.id === threadId
        ? { ...thread, messages: updater(thread.messages), updatedAt: Date.now() }
        : thread)
    }))
  }

  const createChat = () => {
    abortRef.current?.abort()
    const thread = newThread(selectedModelId)
    setChatState(current => ({ threads: [thread, ...current.threads].slice(0, 50), activeId: thread.id }))
    setPrompt('')
    setBusy(false)
    setSidebarOpen(false)
    requestAnimationFrame(() => inputRef.current?.focus())
  }

  const selectThread = (thread: ChatThread) => {
    abortRef.current?.abort()
    setBusy(false)
    setChatState(current => ({ ...current, activeId: thread.id }))
    if (thread.modelId) setSelectedModelId(thread.modelId)
    setSidebarOpen(false)
  }

  const deleteThread = (threadId: string) => {
    setChatState(current => {
      const remaining = current.threads.filter(thread => thread.id !== threadId)
      if (remaining.length) return { threads: remaining, activeId: current.activeId === threadId ? remaining[0].id : current.activeId }
      const replacement = newThread(selectedModelId)
      return { threads: [replacement], activeId: replacement.id }
    })
  }

  const send = async (suggestedPrompt?: string) => {
    const content = (suggestedPrompt ?? prompt).trim()
    if (!content || busy) return
    if (!chatKey) return toast.error('مسیر امن ArkaChat آماده نیست؛ لطفاً دوباره تلاش کنید.')
    if (!selectedModel || !activeThread) return toast.error('یک مدل سازگار با چت انتخاب کنید.')

    const threadId = activeThread.id
    const userMessage: ChatMessage = { id: makeId(), role: 'user', content }
    const assistantId = makeId()
    const assistantMessage: ChatMessage = { id: assistantId, role: 'assistant', content: '', pending: true, modelName: selectedModel.displayName }
    const conversation = [...messages, userMessage]
    setChatState(current => ({
      ...current,
      threads: current.threads.map(thread => thread.id === threadId ? {
        ...thread,
        title: thread.messages.length ? thread.title : content.slice(0, 46),
        modelId: selectedModel.modelId,
        messages: [...conversation, assistantMessage],
        updatedAt: Date.now()
      } : thread)
    }))
    setPrompt('')
    setBusy(true)
    const controller = new AbortController()
    abortRef.current = controller
    const started = performance.now()

    try {
      const usesResponsesApi = selectedModel.endpointPath.replace(/\/$/, '').endsWith('/responses')
      const endpoint = `${API || window.location.origin}${usesResponsesApi ? '/v1/responses' : '/v1/chat/completions'}`
      const requestMessages = conversation.slice(-30).map(message => ({ role: message.role, content: message.content }))
      const response = await fetch(endpoint, {
        method: 'POST',
        signal: controller.signal,
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${chatKey.raw}` },
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
      updateThreadMessages(threadId, current => current.map(message => message.id === assistantId ? {
        ...message,
        content: answer,
        pending: false,
        latencyMs: Math.round(performance.now() - started),
        totalTokens: usage?.total_tokens
      } : message))
    } catch (error) {
      const aborted = error instanceof DOMException && error.name === 'AbortError'
      const safe = aborted ? { message: 'تولید پاسخ متوقف شد.' } : (typeof error === 'object' && error && 'message' in error ? error as { message: string } : safeApiError(error))
      updateThreadMessages(threadId, current => current.map(message => message.id === assistantId ? { ...message, content: safe.message, pending: false, error: !aborted } : message))
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

  return <div className={`arkachat-shell ${sidebarOpen ? 'nav-open' : ''}`}>
    <aside className="arkachat-nav" aria-label="تاریخچه گفتگوهای ArkaChat">
      <header><div className="arkachat-wordmark"><span><Sparkles/></span><b>Arka<em>Chat</em></b></div><button className="arkachat-nav-close" onClick={() => setSidebarOpen(false)} aria-label="بستن تاریخچه"><X/></button></header>
      <button className="arkachat-create" onClick={createChat}><Plus/><span>گفت‌وگوی جدید</span></button>
      <div className="arkachat-history"><small>گفت‌وگوهای اخیر</small>{[...chatState.threads].sort((a, b) => b.updatedAt - a.updatedAt).map(thread => <div key={thread.id} className={`arkachat-history-row ${thread.id === chatState.activeId ? 'active' : ''}`}><button onClick={() => selectThread(thread)}><MessageSquareText/><span><b>{thread.title}</b><small>{thread.messages.length ? `${thread.messages.filter(message => message.role === 'user').length} پیام` : 'هنوز پیامی ندارد'}</small></span></button><button className="arkachat-thread-delete" onClick={() => deleteThread(thread.id)} aria-label={`حذف ${thread.title}`}><Trash2/></button></div>)}</div>
      <footer><div className="arkachat-user"><span>{user.displayName.slice(0, 1)}</span><div><b>{user.displayName}</b><small dir="ltr">{user.mobile}</small></div></div><div className="arkachat-nav-actions"><button onClick={onOpenDashboard}><LayoutDashboard/>داشبورد AiBus</button><button onClick={onLogout}><LogOut/>خروج</button></div></footer>
    </aside>
    {sidebarOpen && <button className="arkachat-overlay" onClick={() => setSidebarOpen(false)} aria-label="بستن تاریخچه"/>}

    <main className="arkachat-main">
      <header className="arkachat-mobile-head"><button onClick={() => setSidebarOpen(true)} aria-label="بازکردن تاریخچه"><Menu/></button><div className="arkachat-wordmark"><span><Sparkles/></span><b>Arka<em>Chat</em></b></div><button onClick={createChat} aria-label="گفت‌وگوی جدید"><Plus/></button></header>
      <section className="arka-chat-page standalone" aria-label="ArkaChat">
        <header className="arka-chat-head">
          <div className="arka-chat-brand"><span><Sparkles/></span><div><small>دستیار چندمدلی مستقل</small><h1>Arka<span>Chat</span></h1></div><em><i/>آنلاین</em></div>
          <div className="arkachat-balance"><Wallet/><span><small>اعتبار حساب</small><b dir="ltr">${usd(user.walletUsd, 2)}</b></span></div>
        </header>

        <div className="arka-model-bar">
          <div className="arka-selected-model">{selectedModel ? <><ProviderMark model={selectedModel}/><span><small>مدل فعال گفتگو</small><b>{selectedModel.displayName}</b><code dir="ltr">{selectedModel.modelId}</code></span><div className="arka-model-flags"><em><Zap/>{selectedModel.supportsStreaming ? 'Stream' : 'Standard'}</em><em><ShieldCheck/>AiBus</em></div></> : <><Bot/><span><b>مدلی در دسترس نیست</b><small>دسترسی مدل‌ها و وضعیت سرویس را بررسی کنید.</small></span></>}</div>
          <div className="arka-model-picker"><Search/><input value={search} onChange={event => setSearch(event.target.value)} placeholder="جست‌وجوی مدل یا شرکت..." aria-label="جست‌وجوی مدل"/><select value={selectedModelId} onChange={event => setSelectedModelId(event.target.value)} aria-label="انتخاب مدل چت"><option value="">انتخاب مدل</option>{visibleModels.map(model => <option key={model.id} value={model.modelId}>{model.provider.name} · {model.displayName}</option>)}</select><span><Check/>{allowedModels.length} مدل قابل استفاده</span></div>
        </div>

        {!loading && !chatKey && <div className="arka-key-alert"><ShieldCheck/><div><b>راه‌اندازی مسیر امن گفتگو انجام نشد.</b><p>از داشبورد وضعیت حساب یا کلیدهای خود را بررسی کنید و دوباره وارد ArkaChat شوید.</p></div><button onClick={onOpenDashboard}>رفتن به داشبورد</button></div>}

        <div className={`arka-conversation ${messages.length ? 'has-messages' : ''}`}>
          <div className="arka-messages" aria-live="polite">
            {loading && <div className="arka-chat-loading"><Sparkles/><span>در حال آماده‌سازی مدل‌ها و مسیر امن گفتگو...</span></div>}
            {!loading && !messages.length && <div className="arka-welcome"><div className="arka-welcome-orb"><Sparkles/><i/><i/><i/></div><span>سلام {user.displayName}، آماده‌ام</span><h2>امروز روی چه چیزی کار کنیم؟</h2><p>مدل دلخواهتان را از بالای صفحه انتخاب کنید؛ هر گفتگو با همان مدل ادامه پیدا می‌کند و در تاریخچه این دستگاه باقی می‌ماند.</p><div className="arka-suggestions">{suggestions.map((suggestion, index) => <button key={suggestion} onClick={() => void send(suggestion)} disabled={!chatKey || !selectedModel}><i>{String(index + 1).padStart(2, '0')}</i><span>{suggestion}</span><ArrowUp/></button>)}</div></div>}
            {messages.map(message => <article key={message.id} className={`arka-message ${message.role} ${message.error ? 'error' : ''}`}><div className="arka-message-avatar">{message.role === 'assistant' ? <Sparkles/> : <UserRound/>}</div><div className="arka-message-body"><header><b>{message.role === 'assistant' ? message.modelName || 'ArkaChat' : 'شما'}</b>{message.role === 'assistant' && !message.pending && !message.error && <span>{message.latencyMs ? `${message.latencyMs}ms` : ''}{message.totalTokens ? ` · ${message.totalTokens} token` : ''}</span>}</header>{message.pending ? <div className="arka-thinking"><i/><i/><i/><span>در حال ساخت پاسخ...</span></div> : <p>{message.content}</p>}{message.role === 'assistant' && message.content && !message.pending && <footer><button onClick={() => { void navigator.clipboard.writeText(message.content); toast.success('پاسخ کپی شد') }}><Copy/>کپی پاسخ</button></footer>}</div></article>)}
            <div ref={messagesEndRef}/>
          </div>

          <div className="arka-composer-wrap"><div className="arka-composer"><textarea ref={inputRef} rows={1} value={prompt} maxLength={12000} onChange={event => setPrompt(event.target.value)} onKeyDown={handleKeyDown} placeholder={selectedModel ? `پیام به ${selectedModel.displayName}...` : 'ابتدا مدل را انتخاب کنید...'} disabled={!chatKey || !selectedModel || busy} aria-label="پیام ArkaChat"/><div className="arka-composer-foot"><span><ShieldCheck/>درخواست امن از مسیر AiBus</span><small dir="ltr">{prompt.length.toLocaleString('en-US')} / 12,000</small>{busy ? <button className="stop" onClick={() => abortRef.current?.abort()} aria-label="توقف پاسخ"><span/></button> : <button className="send" onClick={() => void send()} disabled={!prompt.trim() || !chatKey || !selectedModel} aria-label="ارسال پیام"><ArrowUp/></button>}</div></div><div className="arka-chat-note"><Sparkles/>پاسخ مدل‌های هوش مصنوعی ممکن است دقیق نباشد؛ اطلاعات مهم را بررسی کنید.<button onClick={() => deleteThread(chatState.activeId)}><Trash2/>حذف گفتگو</button></div></div>
        </div>
      </section>
    </main>
  </div>
}
