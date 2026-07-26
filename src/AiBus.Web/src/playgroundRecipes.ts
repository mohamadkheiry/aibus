export type CodeRecipeLanguage='curl'|'javascript'|'python'|'csharp'|'php'|'go'
export type PlaygroundMode='json'|'tts'|'stt'|'realtime'

export const CODE_LANGUAGES:Array<{id:CodeRecipeLanguage;label:string}>=[
  {id:'curl',label:'cURL'},{id:'javascript',label:'JavaScript'},{id:'python',label:'Python'},
  {id:'csharp',label:'C#'},{id:'php',label:'PHP'},{id:'go',label:'Go'}
]

const apiKey='YOUR_AIBUS_API_KEY'
const parse=(json:string)=>{try{return JSON.parse(json) as Record<string,unknown>}catch{return {}}}

export function createCodeRecipe(language:CodeRecipeLanguage,endpoint:string,json:string,mode:PlaygroundMode='json'){
  if(mode==='tts')return ttsRecipe(language,endpoint,json)
  if(mode==='stt')return sttRecipe(language,endpoint,json)
  if(mode==='realtime')return realtimeRecipe(language,endpoint,json)
  const shellJson=json.replace(/'/g,"'\\''")
  switch(language){
    case 'javascript':return `const payload = ${json};\n\nconst response = await fetch('${endpoint}', {\n  method: 'POST',\n  headers: {\n    'Authorization': 'Bearer ${apiKey}',\n    'Content-Type': 'application/json'\n  },\n  body: JSON.stringify(payload)\n});\n\nif (!response.ok) throw new Error(await response.text());\nconst contentType = response.headers.get('content-type') || '';\nconst data = contentType.includes('text/event-stream')\n  ? await response.text()\n  : await response.json();\nconsole.log(data);`
    case 'python':return `import json\nimport requests\n\npayload = json.loads(r'''${json}''')\nresponse = requests.post(\n    '${endpoint}',\n    headers={'Authorization': 'Bearer ${apiKey}', 'Content-Type': 'application/json'},\n    json=payload, stream=bool(payload.get('stream')), timeout=120\n)\nresponse.raise_for_status()\nif payload.get('stream'):\n    for line in response.iter_lines(decode_unicode=True):\n        if line: print(line)\nelse:\n    print(json.dumps(response.json(), ensure_ascii=False, indent=2))`
    case 'csharp':return `using System.Net.Http.Headers;\nusing System.Text;\n\nusing var client = new HttpClient();\nclient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "${apiKey}");\nvar json = """\n${json}\n""";\nusing var content = new StringContent(json, Encoding.UTF8, "application/json");\nusing var response = await client.PostAsync("${endpoint}", content);\nresponse.EnsureSuccessStatusCode();\nConsole.WriteLine(await response.Content.ReadAsStringAsync());`
    case 'php':return `<?php\n$payload = <<<'JSON'\n${json}\nJSON;\n$ch = curl_init('${endpoint}');\ncurl_setopt_array($ch, [CURLOPT_POST => true, CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => ['Authorization: Bearer ${apiKey}', 'Content-Type: application/json'], CURLOPT_POSTFIELDS => $payload]);\n$response = curl_exec($ch);\nif ($response === false) throw new RuntimeException(curl_error($ch));\necho $response;`
    case 'go':return `package main\n\nimport ("bytes"; "fmt"; "io"; "net/http")\nfunc main() {\n  payload := []byte(${JSON.stringify(json)})\n  req, _ := http.NewRequest("POST", "${endpoint}", bytes.NewBuffer(payload))\n  req.Header.Set("Authorization", "Bearer ${apiKey}"); req.Header.Set("Content-Type", "application/json")\n  res, err := http.DefaultClient.Do(req); if err != nil { panic(err) }; defer res.Body.Close()\n  body, _ := io.ReadAll(res.Body); fmt.Println(string(body))\n}`
    default:return `curl --request POST '${endpoint}' \\\n  --header 'Authorization: Bearer ${apiKey}' \\\n  --header 'Content-Type: application/json' \\\n  --data '${shellJson}'`
  }
}

function ttsRecipe(language:CodeRecipeLanguage,endpoint:string,json:string){
  const payload=parse(json), body=JSON.stringify(payload,null,2), shell=body.replace(/'/g,"'\\''")
  switch(language){
    case 'javascript':return `const response = await fetch('${endpoint}', {\n  method: 'POST',\n  headers: { Authorization: 'Bearer ${apiKey}', 'Content-Type': 'application/json' },\n  body: JSON.stringify(${body})\n});\nif (!response.ok) throw new Error(await response.text());\nconst audio = await response.blob();\nconst url = URL.createObjectURL(audio);\nnew Audio(url).play();`
    case 'python':return `import requests\n\npayload = ${JSON.stringify(payload)}\nresponse = requests.post('${endpoint}', headers={'Authorization': 'Bearer ${apiKey}'}, json=payload, timeout=120)\nresponse.raise_for_status()\nwith open('speech.mp3', 'wb') as audio:\n    audio.write(response.content)\nprint('speech.mp3 saved')`
    case 'csharp':return `using System.Net.Http.Headers;\nusing System.Text;\nusing var client = new HttpClient();\nclient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "${apiKey}");\nvar json = """\n${body}\n""";\nusing var content = new StringContent(json, Encoding.UTF8, "application/json");\nusing var response = await client.PostAsync("${endpoint}", content);\nresponse.EnsureSuccessStatusCode();\nawait File.WriteAllBytesAsync("speech.mp3", await response.Content.ReadAsByteArrayAsync());`
    case 'php':return `<?php\n$ch = curl_init('${endpoint}');\ncurl_setopt_array($ch, [CURLOPT_POST => true, CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => ['Authorization: Bearer ${apiKey}', 'Content-Type: application/json'], CURLOPT_POSTFIELDS => '${shell}']);\nfile_put_contents('speech.mp3', curl_exec($ch));`
    case 'go':return `package main\nimport ("bytes"; "io"; "net/http"; "os")\nfunc main(){ body:=[]byte(${JSON.stringify(body)}); req,_:=http.NewRequest("POST","${endpoint}",bytes.NewReader(body)); req.Header.Set("Authorization","Bearer ${apiKey}"); req.Header.Set("Content-Type","application/json"); res,err:=http.DefaultClient.Do(req); if err!=nil{panic(err)}; defer res.Body.Close(); audio,_:=io.ReadAll(res.Body); os.WriteFile("speech.mp3",audio,0644) }`
    default:return `curl --request POST '${endpoint}' \\\n  --header 'Authorization: Bearer ${apiKey}' \\\n  --header 'Content-Type: application/json' \\\n  --data '${shell}' \\\n  --output speech.mp3`
  }
}

function sttRecipe(recipeLanguage:CodeRecipeLanguage,endpoint:string,json:string){
  const payload=parse(json), model=String(payload.model||'MODEL_ID'), language=String(payload.language||'fa')
  switch(recipeLanguage){
    case 'javascript':return `const form = new FormData();\nform.append('model', '${model}');\nform.append('language', '${language}');\nform.append('file', document.querySelector('input[type=file]').files[0]);\nconst response = await fetch('${endpoint}', { method: 'POST', headers: { Authorization: 'Bearer ${apiKey}' }, body: form });\nif (!response.ok) throw new Error(await response.text());\nconsole.log(await response.json());`
    case 'python':return `import requests\nwith open('audio.wav', 'rb') as audio:\n    response = requests.post('${endpoint}', headers={'Authorization': 'Bearer ${apiKey}'}, data={'model': '${model}', 'language': '${language}'}, files={'file': ('audio.wav', audio, 'audio/wav')}, timeout=120)\nresponse.raise_for_status()\nprint(response.json())`
    case 'csharp':return `using System.Net.Http.Headers;\nusing var client = new HttpClient();\nclient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "${apiKey}");\nusing var form = new MultipartFormDataContent();\nform.Add(new StringContent("${model}"), "model"); form.Add(new StringContent("${language}"), "language");\nform.Add(new StreamContent(File.OpenRead("audio.wav")), "file", "audio.wav");\nusing var response = await client.PostAsync("${endpoint}", form); response.EnsureSuccessStatusCode();\nConsole.WriteLine(await response.Content.ReadAsStringAsync());`
    case 'php':return `<?php\n$ch = curl_init('${endpoint}');\ncurl_setopt_array($ch, [CURLOPT_POST => true, CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => ['Authorization: Bearer ${apiKey}'], CURLOPT_POSTFIELDS => ['model'=>'${model}', 'language'=>'${language}', 'file'=>new CURLFile('audio.wav','audio/wav')]]);\necho curl_exec($ch);`
    case 'go':return `package main\nimport ("bytes"; "io"; "mime/multipart"; "net/http"; "os")\nfunc main(){ var body bytes.Buffer; w:=multipart.NewWriter(&body); w.WriteField("model","${model}"); w.WriteField("language","${language}"); part,_:=w.CreateFormFile("file","audio.wav"); f,_:=os.Open("audio.wav"); io.Copy(part,f); w.Close(); req,_:=http.NewRequest("POST","${endpoint}",&body); req.Header.Set("Authorization","Bearer ${apiKey}"); req.Header.Set("Content-Type",w.FormDataContentType()); http.DefaultClient.Do(req) }`
    default:return `curl --request POST '${endpoint}' \\\n  --header 'Authorization: Bearer ${apiKey}' \\\n  --form 'model=${model}' \\\n  --form 'language=${language}' \\\n  --form 'file=@audio.wav'`
  }
}

function realtimeRecipe(language:CodeRecipeLanguage,endpoint:string,json:string){
  const model=String(parse(json).model||'MODEL_ID'), ws=endpoint.replace(/^http/,'ws')+(endpoint.includes('?')?'&':'?')+`model=${encodeURIComponent(model)}`
  const event='{"type":"session.update","session":{"modalities":["audio","text"],"voice":"alloy","turn_detection":{"type":"server_vad"}}}'
  switch(language){
    case 'javascript':return `const socket = new WebSocket('${ws}', ['aibus-realtime', 'aibus-key.${apiKey}']);\nsocket.onopen = () => socket.send(${JSON.stringify(event)});\nsocket.onmessage = event => console.log(JSON.parse(event.data));\n// PCM16/24kHz mono chunks:\n// socket.send(JSON.stringify({ type: 'input_audio_buffer.append', audio: base64Pcm16 }));`
    case 'python':return `import asyncio, json, websockets\nasync def main():\n    async with websockets.connect('${ws}', subprotocols=['aibus-realtime', 'aibus-key.${apiKey}']) as socket:\n        await socket.send(${JSON.stringify(event)})\n        async for message in socket: print(json.loads(message))\nasyncio.run(main())`
    case 'csharp':return `using System.Net.WebSockets; using System.Text;\nusing var ws = new ClientWebSocket();\nws.Options.AddSubProtocol("aibus-realtime"); ws.Options.AddSubProtocol("aibus-key.${apiKey}");\nawait ws.ConnectAsync(new Uri("${ws}"), CancellationToken.None);\nvar data = Encoding.UTF8.GetBytes(${JSON.stringify(event)});\nawait ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);`
    case 'php':return `<?php\n// composer require ratchet/pawl\n// Connect to ${ws} with subprotocols:\n$protocols = ['aibus-realtime', 'aibus-key.${apiKey}'];\n// Send after connect:\n$sessionUpdate = ${JSON.stringify(event)};`
    case 'go':return `package main\nimport ("net/http"; "github.com/gorilla/websocket")\nfunc main(){ d:=websocket.Dialer{Subprotocols:[]string{"aibus-realtime","aibus-key.${apiKey}"}}; c,_,err:=d.Dial("${ws}",http.Header{}); if err!=nil{panic(err)}; defer c.Close(); c.WriteMessage(websocket.TextMessage,[]byte(${JSON.stringify(event)})) }`
    default:return `# WebSocket تست تعاملی با wscat\nnpx wscat -c '${ws}' \\\n  -s 'aibus-realtime' \\\n  -s 'aibus-key.${apiKey}'\n\n# سپس این event را ارسال کنید:\n${event}`
  }
}
