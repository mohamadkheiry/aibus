package ir.arka.arkacode

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

class AiBusClient {
    fun models(baseUrl: String, apiKey: String): List<AiModel> {
        val result = request("${normalize(baseUrl)}/v1/models", apiKey, null)
        val data = JSONObject(result).getJSONArray("data")
        return (0 until data.length()).map { index ->
            val item = data.getJSONObject(index)
            AiModel(item.getString("id"), item.optString("display_name", item.getString("id")), item.optString("owned_by", "AiBus"), item.optString("endpoint_path", "/v1/chat/completions"))
        }.filter { model ->
            val endpoint = model.endpointPath.trimEnd('/')
            endpoint.endsWith("/chat/completions", true) || endpoint.endsWith("/responses", true)
        }.sortedWith(compareBy({ it.provider }, { it.displayName }))
    }

    fun runAgent(baseUrl: String, apiKey: String, model: AiModel, effort: String, task: String, files: List<WorkspaceFile>): AgentResult {
        val system = """You are ArkaCode, a careful senior software-engineering agent. Return ONLY valid JSON: {"summary":"short result","explanation":"concise explanation","operations":[{"path":"relative/path","action":"write","content":"complete file content"}]}. Never use absolute paths or ../. Include only required changes. The user reviews all operations before apply."""
        val context = files.joinToString("\n\n") { "--- FILE: ${it.path} ---\n${it.content}" }
        val prompt = "Reasoning level: $effort\n\nUSER TASK:\n$task\n\nWORKSPACE SNAPSHOT:\n$context"
        val responses = model.endpointPath.trimEnd('/').endsWith("/responses", true)
        val input = JSONArray().put(JSONObject().put("role", "system").put("content", system)).put(JSONObject().put("role", "user").put("content", prompt))
        val payload = JSONObject().put("model", model.id).put(if (responses) "input" else "messages", input).put("stream", false)
        if (responses) payload.put("reasoning", JSONObject().put("effort", effort))
        val rawResponse = request("${normalize(baseUrl)}${if (responses) "/v1/responses" else "/v1/chat/completions"}", apiKey, payload.toString())
        val text = extractText(JSONObject(rawResponse))
        val clean = stripFence(text)
        return try {
            val result = JSONObject(clean)
            val operationsJson = result.optJSONArray("operations") ?: JSONArray()
            val operations = (0 until operationsJson.length()).map { index ->
                val item = operationsJson.getJSONObject(index)
                FileOperation(item.getString("path"), item.optString("content", ""), item.optString("action", "write"))
            }
            AgentResult(result.optString("summary", "Agent completed"), result.optString("explanation", ""), operations, text)
        } catch (_: Exception) { AgentResult("پاسخ عامل دریافت شد", text, emptyList(), text) }
    }

    private fun request(url: String, apiKey: String, body: String?): String {
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.requestMethod = if (body == null) "GET" else "POST"
        connection.connectTimeout = 20_000
        connection.readTimeout = 300_000
        connection.setRequestProperty("Authorization", "Bearer ${apiKey.trim()}")
        connection.setRequestProperty("Accept", "application/json")
        connection.setRequestProperty("User-Agent", "ArkaCode-Android/1.0")
        if (body != null) {
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json")
            connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
        }
        val stream = if (connection.responseCode in 200..299) connection.inputStream else connection.errorStream
        val response = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
        if (connection.responseCode !in 200..299) {
            val message = try { JSONObject(response).optJSONObject("error")?.optString("message") ?: JSONObject(response).optString("message") } catch (_: Exception) { "" }
            throw IllegalStateException(message.ifBlank { "AiBus request failed (${connection.responseCode})." })
        }
        return response
    }

    private fun extractText(root: JSONObject): String {
        root.optJSONArray("choices")?.takeIf { it.length() > 0 }?.getJSONObject(0)?.optJSONObject("message")?.opt("content")?.let { content ->
            if (content is String) return content
            if (content is JSONArray) return (0 until content.length()).joinToString("\n") { content.getJSONObject(it).optString("text") }
        }
        root.optString("output_text").takeIf { it.isNotBlank() }?.let { return it }
        root.optJSONArray("output")?.let { output ->
            return (0 until output.length()).flatMap { index ->
                val content = output.getJSONObject(index).optJSONArray("content") ?: JSONArray()
                (0 until content.length()).map { content.getJSONObject(it).optString("text") }
            }.filter { it.isNotBlank() }.joinToString("\n")
        }
        return root.toString()
    }

    private fun normalize(value: String) = value.trim().trimEnd('/')
    private fun stripFence(value: String): String {
        val clean = value.trim()
        if (!clean.startsWith("```")) return clean
        val first = clean.indexOf('\n'); val last = clean.lastIndexOf("```")
        return if (first >= 0 && last > first) clean.substring(first + 1, last).trim() else clean
    }
}
