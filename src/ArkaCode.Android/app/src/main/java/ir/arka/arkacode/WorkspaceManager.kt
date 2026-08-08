package ir.arka.arkacode

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

class WorkspaceManager(private val resolver: ContentResolver) {
    private data class Node(val uri: Uri, val path: String, val name: String, val isDirectory: Boolean, val mime: String)
    private val extensions = setOf("kt", "kts", "java", "cs", "xaml", "tsx", "ts", "js", "jsx", "json", "py", "go", "rs", "cpp", "h", "css", "html", "md", "xml", "yml", "yaml", "sql", "sh", "ps1")

    fun snapshot(tree: Uri, maxFiles: Int = 45, maxCharacters: Int = 60000): List<WorkspaceFile> {
        var remaining = maxCharacters
        val result = mutableListOf<WorkspaceFile>()
        for (node in walk(tree).filter { !it.isDirectory && it.name.substringAfterLast('.', "").lowercase() in extensions }.take(maxFiles)) {
            val text = try { resolver.openInputStream(node.uri)?.bufferedReader()?.use { it.readText() }.orEmpty() } catch (_: Exception) { "" }
            if (text.isBlank()) continue
            val selected = text.take(remaining)
            result += WorkspaceFile(node.path, selected)
            remaining -= selected.length
            if (remaining <= 0) break
        }
        return result
    }

    fun apply(tree: Uri, operations: List<FileOperation>): Int {
        val safe = operations.filter { it.action.equals("write", true) && safePath(it.path) }
        val backupRoot = ensureDirectory(tree, listOf(".arkacode", "backups", LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss"))))
        var count = 0
        for (operation in safe) {
            val parts = operation.path.replace('\\', '/').split('/').filter { it.isNotBlank() }
            val parent = ensureDirectory(tree, parts.dropLast(1))
            val existing = findChild(parent, parts.last())
            if (existing != null && !existing.isDirectory) {
                val backupParent = ensureDirectory(backupRoot, parts.dropLast(1))
                val backup = DocumentsContract.createDocument(resolver, backupParent, existing.mime.ifBlank { "text/plain" }, parts.last())
                if (backup != null) resolver.openInputStream(existing.uri)?.use { input -> resolver.openOutputStream(backup, "w")?.use { output -> input.copyTo(output) } }
            }
            val target = existing?.uri ?: DocumentsContract.createDocument(resolver, parent, "text/plain", parts.last()) ?: error("Cannot create ${operation.path}")
            resolver.openOutputStream(target, "wt")?.bufferedWriter(Charsets.UTF_8)?.use { it.write(operation.content) } ?: error("Cannot write ${operation.path}")
            count++
        }
        return count
    }

    private fun walk(tree: Uri): Sequence<Node> = sequence {
        val rootId = DocumentsContract.getTreeDocumentId(tree)
        val root = DocumentsContract.buildDocumentUriUsingTree(tree, rootId)
        suspend fun SequenceScope<Node>.visit(parent: Uri, prefix: String) {
            val id = DocumentsContract.getDocumentId(parent)
            val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, id)
            resolver.query(children, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)?.use { cursor ->
                val nodes = mutableListOf<Node>()
                while (cursor.moveToNext()) {
                    val child = DocumentsContract.buildDocumentUriUsingTree(tree, cursor.getString(0))
                    val name = cursor.getString(1) ?: "untitled"
                    val mime = cursor.getString(2) ?: ""
                    val path = if (prefix.isBlank()) name else "$prefix/$name"
                    if (!path.split('/').any { it in setOf(".git", "node_modules", "bin", "obj", "build", "dist") }) nodes += Node(child, path, name, mime == DocumentsContract.Document.MIME_TYPE_DIR, mime)
                }
                for (node in nodes) { yield(node); if (node.isDirectory) visit(node.uri, node.path) }
            }
        }
        visit(root, "")
    }

    private fun ensureDirectory(treeOrParent: Uri, parts: List<String>): Uri {
        var current = treeOrParent
        if (DocumentsContract.isTreeUri(current)) current = DocumentsContract.buildDocumentUriUsingTree(current, DocumentsContract.getTreeDocumentId(current))
        for (part in parts) {
            val existing = findChild(current, part)
            current = existing?.takeIf { it.isDirectory }?.uri ?: DocumentsContract.createDocument(resolver, current, DocumentsContract.Document.MIME_TYPE_DIR, part) ?: error("Cannot create $part")
        }
        return current
    }

    private fun findChild(parent: Uri, name: String): Node? {
        val tree = parent
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getDocumentId(parent))
        resolver.query(children, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) if (cursor.getString(1) == name) {
                val mime = cursor.getString(2) ?: ""
                return Node(DocumentsContract.buildDocumentUriUsingTree(tree, cursor.getString(0)), name, name, mime == DocumentsContract.Document.MIME_TYPE_DIR, mime)
            }
        }
        return null
    }

    private fun safePath(path: String): Boolean {
        val clean = path.replace('\\', '/')
        return clean.isNotBlank() && !clean.startsWith('/') && !clean.contains(":") && clean.split('/').none { it == ".." || it.isBlank() }
    }
}
