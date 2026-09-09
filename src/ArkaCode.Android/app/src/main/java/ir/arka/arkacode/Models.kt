package ir.arka.arkacode

data class AiModel(val id: String, val displayName: String, val provider: String, val endpointPath: String) {
    override fun toString(): String = "$provider  ·  $displayName"
}
data class WorkspaceFile(val path: String, val content: String)
data class FileOperation(val path: String, val content: String, val action: String = "write")
data class AgentResult(val summary: String, val explanation: String, val operations: List<FileOperation>, val raw: String)
