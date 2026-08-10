import { useEffect, useMemo, useRef, useState } from 'react'
import toast from 'react-hot-toast'
import { Bot, Code2, Copy, Download, FileAudio, Headphones, Mic2, Radio, RefreshCw, ShieldCheck, Square, Upload, X, Zap } from 'lucide-react'
import { API, type Model, request, safeApiError, safeApiErrorFromText, type UserKey } from './api'
import { CODE_LANGUAGES, createCodeRecipe, type CodeRecipeLanguage, type PlaygroundMode } from './playgroundRecipes'

const modeFor=(model:Model):PlaygroundMode=>model.supportsWebSocket?'realtime':model.serviceType==='text_to_speech'||model.serviceType==='voice_clone'||model.serviceType==='voice_design'?'tts':model.serviceType==='speech_to_speech'||model.serviceType==='realtime_translation'?'realtime':model.serviceType==='speech_to_text'||model.serviceType==='translation'?'stt':'json'
const gatewayPath=(mode:PlaygroundMode,model:Model)=>mode==='tts'?'/v1/audio/speech':mode==='stt'?'/v1/audio/transcriptions':mode==='realtime'?'/v1/realtime':model.endpointPath||'/v1/chat/completions'
const serviceNames:Record<string,string>={chat:'متن و چت',coding:'کدنویسی',search:'جستجو',deep_research:'پژوهش عمیق',computer_use:'کار با رایانه',embeddings:'بردارسازی متن',moderation:'ایمنی محتوا',image_generation:'تولید تصویر',video_generation:'تولید ویدیو',audio_understanding:'درک صوت',speech_to_text:'صوت به متن',text_to_speech:'متن به صوت',speech_to_speech:'صوت به صوت',realtime_translation:'ترجمه هم‌زمان',translation:'ترجمه و دوبله',voice_clone:'شبیه‌سازی صدا',voice_design:'طراحی صدا'}

const initialPayload=(model:Model,mode:PlaygroundMode)=>{
  const realtimePayload:Record<string,unknown>=model.serviceType==='speech_to_text'?{model:model.modelId,language:'fa',delay:'low'}:model.serviceType==='realtime_translation'?{model:model.modelId,target_language:'fa'}:{model:model.modelId,voice:'alloy',instructions:'به زبان فارسی، کوتاه و طبیعی پاسخ بده.',modalities:['audio','text']}
  const fallback:Record<string,unknown>=mode==='tts'?{model:model.modelId,input:'سلام! این یک آزمایش زنده تولید گفتار در AiBus است.',voice:'alloy',response_format:'mp3'}:mode==='stt'?{model:model.modelId,language:'fa',response_format:'json'}:mode==='realtime'?realtimePayload:{model:model.modelId,messages:[{role:'user',content:'در سه جمله کوتاه توضیح بده هوش مصنوعی چگونه بهره‌وری یک کسب‌وکار را افزایش می‌دهد.'}],stream:false}
  try{return JSON.stringify({...fallback,...JSON.parse(model.testPayloadJson||'{}'),model:model.modelId},null,2)}catch{return JSON.stringify(fallback,null,2)}
}

const pcm16Base64=(samples:Float32Array,inputRate:number)=>{
  const ratio=inputRate/24000,length=Math.max(1,Math.floor(samples.length/ratio)),pcm=new Int16Array(length)
  for(let i=0;i<length;i++){const start=Math.floor(i*ratio),end=Math.min(samples.length,Math.floor((i+1)*ratio));let sum=0;for(let j=start;j<end;j++)sum+=samples[j];const value=Math.max(-1,Math.min(1,sum/Math.max(1,end-start)));pcm[i]=value<0?value*32768:value*32767}
  const bytes=new Uint8Array(pcm.buffer);let binary='';for(let i=0;i<bytes.length;i++)binary+=String.fromCharCode(bytes[i]);return btoa(binary)
}

const audioFromJson=(value:unknown):{data:string;mime:string}|null=>{
  if(!value||typeof value!=='object')return null
  for(const [key,item] of Object.entries(value as Record<string,unknown>)){
    if(typeof item==='string'&&item.length>128&&(key.toLowerCase().includes('audio')||key.toLowerCase()==='data'))return {data:item.includes(',')?item.split(',').pop()!:item,mime:'audio/mpeg'}
    const nested=audioFromJson(item);if(nested)return nested
  }
  return null
}

const base64Blob=(data:string,mime:string)=>{const binary=atob(data),bytes=new Uint8Array(binary.length);for(let i=0;i<binary.length;i++)bytes[i]=binary.charCodeAt(i);return new Blob([bytes],{type:mime})}

export function Playground({model,close}:{model:Model;close:()=>void}){
  const mode=modeFor(model),initial=useMemo(()=>initialPayload(model,mode),[model,mode])
  const [keys,setKeys]=useState<UserKey[]>([]),[key,setKey]=useState(()=>sessionStorage.getItem('aibus_test_key')||''),[requestJson,setRequestJson]=useState(initial),[output,setOutput]=useState(''),[busy,setBusy]=useState(false),[language,setLanguage]=useState<CodeRecipeLanguage>('curl'),[meta,setMeta]=useState<{status:number;latency:number}|null>(null)
  const [audioFile,setAudioFile]=useState<File|null>(null),[audioUrl,setAudioUrl]=useState(''),[duration,setDuration]=useState(0),[recording,setRecording]=useState(false),[live,setLive]=useState<'idle'|'connecting'|'connected'>('idle'),[transcript,setTranscript]=useState(''),[events,setEvents]=useState<string[]>([])
  const mediaRecorder=useRef<MediaRecorder|null>(null),recordChunks=useRef<Blob[]>([]),recordStarted=useRef(0),mediaStream=useRef<MediaStream|null>(null),socket=useRef<WebSocket|null>(null),audioContext=useRef<AudioContext|null>(null),processor=useRef<ScriptProcessorNode|null>(null),sourceNode=useRef<MediaStreamAudioSourceNode|null>(null),playAt=useRef(0)
  const origin=API||window.location.origin,endpoint=`${origin}${gatewayPath(mode,model)}`,shownEndpoint=mode==='realtime'?endpoint.replace(/^http/,'ws'):endpoint
  const recipe=useMemo(()=>createCodeRecipe(language,endpoint,requestJson,mode,model.serviceType),[language,endpoint,requestJson,mode,model.serviceType])

  useEffect(()=>{request<UserKey[]>('/api/keys').then(setKeys).catch(()=>{})},[])
  useEffect(()=>()=>{if(audioUrl)URL.revokeObjectURL(audioUrl)},[audioUrl])
  useEffect(()=>()=>stopLive(false),[])

  const payload=()=>{const parsed=JSON.parse(requestJson) as Record<string,unknown>;if(!parsed||Array.isArray(parsed)||typeof parsed!=='object')throw new Error('ورودی باید یک JSON Object معتبر باشد.');if(!parsed.model)throw new Error('فیلد model در ورودی JSON الزامی است.');return parsed}
  const prepare=()=>{if(!key)throw new Error('کلید API را وارد کنید.');sessionStorage.setItem('aibus_test_key',key);setBusy(true);setOutput('');setMeta(null);return performance.now()}
  const finishError=(error:unknown)=>{const safe=safeApiError(error);setOutput(JSON.stringify({error:safe},null,2));toast.error(safe.message)}
  const showUpstreamError=(text:string,status:number,started:number)=>{let raw:unknown;try{raw=JSON.parse(text)}catch{raw={raw:text}};setOutput(JSON.stringify(raw,null,2));setMeta({status,latency:Math.round(performance.now()-started)});toast.error(safeApiErrorFromText(text,status).message)}
  const formatInput=()=>{try{setRequestJson(JSON.stringify(JSON.parse(requestJson),null,2));toast.success('JSON مرتب شد')}catch{toast.error('ساختار JSON معتبر نیست.')}}
  const updateSpeechText=(value:string)=>{try{const body=payload();body.input=value;setRequestJson(JSON.stringify(body,null,2))}catch{toast.error('ابتدا ساختار JSON را اصلاح کنید.')}}
  const setAudio=(blob:Blob,name='audio.webm')=>{if(audioUrl)URL.revokeObjectURL(audioUrl);const file=blob instanceof File?blob:new File([blob],name,{type:blob.type||'audio/webm'}),url=URL.createObjectURL(file);setAudioFile(file);setAudioUrl(url);const probe=new Audio(url);probe.onloadedmetadata=()=>Number.isFinite(probe.duration)&&setDuration(probe.duration)}

  const run=async()=>{
    if(mode==='realtime')return startLive()
    let started=0
    try{
      const body=payload();started=prepare()
      if(mode==='stt'){
        if(!audioFile)throw new Error('یک فایل صوتی انتخاب یا با میکروفون ضبط کنید.')
        const form=new FormData();Object.entries(body).forEach(([name,value])=>{if(name!=='file'&&value!=null&&(typeof value!=='object'||Array.isArray(value)))form.append(name,typeof value==='string'?value:JSON.stringify(value))});form.append('file',audioFile,audioFile.name);form.append('aibus_duration_seconds',String(duration||0))
        const res=await fetch(endpoint,{method:'POST',headers:{Authorization:`Bearer ${key}`},body:form}),text=await res.text()
        if(!res.ok){showUpstreamError(text,res.status,started);return}
        let data:unknown;try{data=JSON.parse(text)}catch{data={raw:text}}
        setOutput(JSON.stringify(data,null,2));setMeta({status:res.status,latency:Math.round(performance.now()-started)});toast.success('رونویسی صوت دریافت شد')
      }else{
        const res=await fetch(endpoint,{method:'POST',headers:{Authorization:`Bearer ${key}`,'Content-Type':'application/json'},body:JSON.stringify(body)}),contentType=res.headers.get('content-type')||''
        if(!res.ok){showUpstreamError(await res.text(),res.status,started);return}
        if(mode==='tts'){
          let blob:Blob,json:unknown=null
          if(contentType.includes('json')){json=await res.json();const encoded=audioFromJson(json);if(!encoded){setOutput(JSON.stringify(json,null,2));throw new Error('پاسخ JSON دریافت شد اما داده صوتی در آن پیدا نشد.')}blob=base64Blob(encoded.data,encoded.mime)}else blob=await res.blob()
          setAudio(blob,`speech-${model.modelId}.mp3`);setOutput(JSON.stringify({object:'aibus.audio',model:model.modelId,contentType:blob.type||contentType,sizeBytes:blob.size,playable:true},null,2));toast.success('صدای تولیدشده آماده پخش است')
        }else{const text=await res.text();let data:unknown;try{data=JSON.parse(text)}catch{data={raw:text}};setOutput(JSON.stringify(data,null,2))}
        setMeta({status:res.status,latency:Math.round(performance.now()-started)})
      }
    }catch(error){finishError(error)}finally{setBusy(false)}
  }

  const toggleRecord=async()=>{
    if(recording){mediaRecorder.current?.stop();setRecording(false);return}
    try{const stream=await navigator.mediaDevices.getUserMedia({audio:true});mediaStream.current=stream;recordChunks.current=[];const recorder=new MediaRecorder(stream);mediaRecorder.current=recorder;recordStarted.current=performance.now();recorder.ondataavailable=e=>e.data.size&&recordChunks.current.push(e.data);recorder.onstop=()=>{const blob=new Blob(recordChunks.current,{type:recorder.mimeType||'audio/webm'});setDuration((performance.now()-recordStarted.current)/1000);setAudio(blob,'microphone.webm');stream.getTracks().forEach(track=>track.stop())};recorder.start(250);setRecording(true)}catch(error){toast.error(error instanceof Error?error.message:'دسترسی میکروفون ممکن نیست.')}
  }

  const playPcm=(base64:string)=>{const context=audioContext.current;if(!context)return;const binary=atob(base64),bytes=new Uint8Array(binary.length);for(let i=0;i<binary.length;i++)bytes[i]=binary.charCodeAt(i);const pcm=new Int16Array(bytes.buffer),buffer=context.createBuffer(1,pcm.length,24000),channel=buffer.getChannelData(0);for(let i=0;i<pcm.length;i++)channel[i]=pcm[i]/32768;const source=context.createBufferSource();source.buffer=buffer;source.connect(context.destination);const start=Math.max(context.currentTime,playAt.current);source.start(start);playAt.current=start+buffer.duration}
  const stopLive=(update=true)=>{processor.current?.disconnect();sourceNode.current?.disconnect();mediaStream.current?.getTracks().forEach(track=>track.stop());if(socket.current&&socket.current.readyState<2)socket.current.close(1000,'user stopped');void audioContext.current?.close();processor.current=null;sourceNode.current=null;mediaStream.current=null;socket.current=null;audioContext.current=null;if(update)setLive('idle')}
  const startLive=async()=>{
    if(live!=='idle'){stopLive();return}
    try{
      const body=payload();if(!key)throw new Error('کلید API را وارد کنید.');sessionStorage.setItem('aibus_test_key',key);setLive('connecting');setTranscript('');setEvents([])
      const stream=await navigator.mediaDevices.getUserMedia({audio:{echoCancellation:true,noiseSuppression:true,channelCount:1}});mediaStream.current=stream
      const context=new AudioContext();audioContext.current=context;await context.resume();playAt.current=context.currentTime
      const wsUrl=`${shownEndpoint}?model=${encodeURIComponent(model.modelId)}`,ws=new WebSocket(wsUrl,['aibus-realtime',`aibus-key.${key}`]);socket.current=ws
      ws.onopen=()=>{setLive('connected');const voice=String(body.voice||'alloy'),instructions=String(body.instructions||'به زبان فارسی پاسخ بده.'),language=String(body.language||'fa'),targetLanguage=String(body.target_language||'fa');const session=model.serviceType==='speech_to_text'?{type:'session.update',session:{type:'transcription',audio:{input:{format:{type:'audio/pcm',rate:24000},transcription:{model:model.modelId,language,delay:String(body.delay||'low')},turn_detection:null}}}}:model.serviceType==='realtime_translation'?{type:'session.update',session:{audio:{output:{language:targetLanguage}}}}:{type:'session.update',session:{type:'realtime',instructions,output_modalities:['audio'],audio:{input:{format:{type:'audio/pcm',rate:24000},transcription:{model:'gpt-4o-mini-transcribe'},turn_detection:{type:'server_vad'}},output:{format:{type:'audio/pcm',rate:24000},voice}}}};ws.send(JSON.stringify(session));const input=context.createMediaStreamSource(stream),node=context.createScriptProcessor(4096,1,1),silent=context.createGain();silent.gain.value=0;input.connect(node);node.connect(silent);silent.connect(context.destination);sourceNode.current=input;processor.current=node;node.onaudioprocess=e=>{if(ws.readyState===WebSocket.OPEN)ws.send(JSON.stringify({type:model.serviceType==='realtime_translation'?'session.input_audio_buffer.append':'input_audio_buffer.append',audio:pcm16Base64(e.inputBuffer.getChannelData(0),context.sampleRate)}))}}
      ws.onmessage=event=>{try{const data=JSON.parse(String(event.data)) as Record<string,unknown>,type=String(data.type||'event');setEvents(items=>[...items.slice(-79),`${new Date().toLocaleTimeString('fa-IR')} · ${type}`]);const delta=typeof data.delta==='string'?data.delta:'',completed=typeof data.transcript==='string'?data.transcript:'';if(type.includes('transcript')&&delta)setTranscript(value=>value+delta);else if(type.includes('transcript')&&completed)setTranscript(value=>`${value}${value?'\n':''}${completed}`);if((type==='response.audio.delta'||type==='response.output_audio.delta'||type==='session.output_audio.delta')&&delta)playPcm(delta);if(type==='error'||'error' in data)toast.error(safeApiError(data).message)}catch{setEvents(items=>[...items.slice(-79),'پیام باینری/ناشناخته دریافت شد'])}}
      ws.onerror=()=>{toast.error(safeApiError({error:{code:'provider_unavailable'}}).message);stopLive()};ws.onclose=()=>stopLive()
    }catch(error){stopLive();toast.error(safeApiError(error).message)}
  }

  const outputView=mode==='realtime'?<div className="live-output"><div className={`live-status ${live}`}><i/><span>{live==='connected'?'میکروفون فعال و مکالمه برقرار است':live==='connecting'?'در حال اتصال امن...':'آماده شروع مکالمه'}</span></div><section><b>متن مکالمه</b><p>{transcript||'متن صحبت شما و پاسخ مدل در این بخش ظاهر می‌شود.'}</p></section><section><b>رویدادهای WebSocket</b><pre dir="ltr">{events.join('\n')||'هنوز رویدادی دریافت نشده است.'}</pre></section></div>:busy?<div className="thinking"><i/><i/><i/>در حال دریافت پاسخ...</div>:output?<pre dir="ltr">{output}</pre>:<div className="output-empty">{mode==='tts'?<Headphones/>:mode==='stt'?<FileAudio/>:<Bot/>}<p>{mode==='tts'?'صدای خروجی اینجا آماده پخش می‌شود.':mode==='stt'?'متن رونویسی‌شده اینجا نمایش داده می‌شود.':'پاسخ کامل JSON اینجا نمایش داده می‌شود.'}</p></div>

  return <div className="modal-layer" onMouseDown={e=>e.target===e.currentTarget&&close()}><div className="modal wide media-lab-modal"><header><div><h3>آزمایش {model.displayName}</h3><p>{serviceNames[model.serviceType]||model.serviceType} · ورودی و خروجی واقعی</p></div><button className="icon-btn" aria-label="بستن آزمایشگاه" onClick={close}><X/></button></header><div className="modal-body"><div className="playground playground-pro media-playground"><div className="play-column"><div className="endpoint-strip"><span>AiBus Endpoint</span><code dir="ltr">{shownEndpoint}</code><span className={`badge ${mode==='realtime'?'violet':'outline'}`}>{mode==='realtime'?'WebSocket':model.supportsStreaming?'HTTP / Stream':'HTTP'}</span></div><details className="source-endpoint"><summary>مشاهده مسیر اصلی ارائه‌دهنده</summary><code dir="ltr">{model.endpointPath}</code></details><label className="field"><span>کلید AiBus</span><input aria-label="کلید AiBus آزمایشگاه" dir="ltr" value={key} onChange={e=>setKey(e.target.value)} placeholder="aibus_••••••••••••"/><small>{keys.length?`${keys.length} کلید در پنل دارید؛ مقدار کامل کلید ذخیره‌شده را وارد کنید.`:'ابتدا از بخش کلیدها یک API Key بسازید.'}</small></label>{mode==='tts'&&<label className="field media-text"><span>متن موردنظر برای تولید صدا</span><textarea rows={5} value={String((()=>{try{return payload().input||''}catch{return ''}})())} onChange={e=>updateSpeechText(e.target.value)} placeholder="متن فارسی یا انگلیسی را وارد کنید..."/></label>}{mode==='stt'&&<div className="audio-input-card"><header><FileAudio/><div><b>فایل یا صدای میکروفون</b><small>MP3، WAV، M4A، OGG و WebM</small></div></header><div className="audio-actions"><label className="ghost-btn"><Upload/>انتخاب فایل<input type="file" accept="audio/*" onChange={e=>e.target.files?.[0]&&setAudio(e.target.files[0])}/></label><button className={`ghost-btn ${recording?'recording':''}`} onClick={toggleRecord}>{recording?<><Square/>پایان ضبط</>:<><Mic2/>ضبط با میکروفون</>}</button></div>{audioUrl&&<audio controls src={audioUrl}/>} {audioFile&&<small>{audioFile.name} · {(audioFile.size/1024).toFixed(1)} KB · {duration?`${duration.toFixed(1)} ثانیه`:''}</small>}</div>}{mode==='realtime'&&<div className="voice-console"><div className="voice-orb"><Radio/><i/><i/><i/></div><h4>مکالمه صوتی زنده</h4><p>با اجازه شما، صدای میکروفون به PCM16 تبدیل و از اتصال WebSocket امن ارسال می‌شود؛ پاسخ صوتی همان لحظه پخش خواهد شد.</p><button className={`primary-btn full live-button ${live==='connected'?'danger':''}`} onClick={startLive}>{live==='connecting'?<RefreshCw className="spin"/>:live==='connected'?<Square/>:<Mic2/>}{live==='connected'?'پایان مکالمه':'شروع مکالمه زنده'}</button></div>}<details className="advanced-json" open={mode==='json'}><summary><Code2/>ورودی JSON قابل ویرایش</summary><div className="json-field"><header><span><Code2/>Payload درخواست</span><button type="button" onClick={formatInput}>مرتب‌سازی JSON</button></header><textarea dir="ltr" spellCheck={false} value={requestJson} onChange={e=>setRequestJson(e.target.value)} aria-label="ورودی JSON"/></div></details>{mode!=='realtime'&&<button className="primary-btn full" onClick={run} disabled={busy}>{busy?<RefreshCw className="spin"/>:<Zap/>}{mode==='tts'?'تولید صدای واقعی':mode==='stt'?'تبدیل صوت به متن':'ارسال درخواست واقعی'}</button>}</div><div className="output-panel json-output media-output"><header><span><span className="live-dot"/>{mode==='tts'?'خروجی صوت و JSON':mode==='stt'?'متن و JSON خروجی':mode==='realtime'?'خروجی زنده WebSocket':'خروجی JSON'}</span><div className="output-meta">{meta&&<><span className={`badge ${meta.status<400?'mint':'danger'}`}>HTTP {meta.status}</span><small>{meta.latency}ms</small></>}<button className="icon-btn" onClick={()=>{navigator.clipboard.writeText(output||transcript);toast.success('خروجی کپی شد')}} disabled={!output&&!transcript}><Copy/></button></div></header>{mode==='tts'&&audioUrl&&<div className="generated-audio"><div><Headphones/><span><b>صدای تولیدشده</b><small>{model.displayName}</small></span></div><audio controls autoPlay src={audioUrl}/><a className="ghost-btn compact" href={audioUrl} download={`speech-${model.modelId}.mp3`}><Download/>دانلود صدا</a></div>}<div className="media-output-body">{outputView}</div></div><section className="code-recipes"><header><div><Code2/><span><b>نمونه فراخوانی {mode==='realtime'?'WebSocket':mode==='tts'?'متن به صوت':mode==='stt'?'صوت به متن':'API'}</b><small>نمونه‌ها آماده اجرا و متناسب با پروتکل همین آزمایش هستند.</small></span></div><button className="ghost-btn compact" onClick={()=>{navigator.clipboard.writeText(recipe);toast.success('نمونه کد کپی شد')}}><Copy/>کپی کد</button></header><nav>{CODE_LANGUAGES.map(item=><button key={item.id} className={language===item.id?'active':''} onClick={()=>setLanguage(item.id)}>{item.label}</button>)}</nav><pre dir="ltr"><code>{recipe}</code></pre><footer><ShieldCheck/>کلید واقعی در نمونه‌کد قرار نمی‌گیرد؛ مقدار <code>YOUR_AIBUS_API_KEY</code> را در محیط امن جایگزین کنید.</footer></section></div></div></div></div>
}
