package ir.arka.arkacode

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

class MainActivity : Activity() {
    private val executor = Executors.newSingleThreadExecutor()
    private val client = AiBusClient()
    private lateinit var workspace: WorkspaceManager
    private var workspaceUri: Uri? = null
    private var models: List<AiModel> = emptyList()
    private var pendingOperations: List<FileOperation> = emptyList()

    private lateinit var baseUrl: EditText
    private lateinit var apiKey: EditText
    private lateinit var modelSpinner: Spinner
    private lateinit var effortGroup: RadioGroup
    private lateinit var workspaceLabel: TextView
    private lateinit var taskInput: EditText
    private lateinit var output: TextView
    private lateinit var status: TextView
    private lateinit var runButton: Button
    private lateinit var applyButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        window.statusBarColor = getColor(R.color.arka_bg)
        workspace = WorkspaceManager(contentResolver)
        baseUrl = findViewById(R.id.baseUrlInput); apiKey = findViewById(R.id.apiKeyInput); modelSpinner = findViewById(R.id.modelSpinner)
        effortGroup = findViewById(R.id.effortGroup); workspaceLabel = findViewById(R.id.workspaceLabel); taskInput = findViewById(R.id.taskInput)
        output = findViewById(R.id.outputText); status = findViewById(R.id.statusBadge); runButton = findViewById(R.id.runButton); applyButton = findViewById(R.id.applyButton)
        findViewById<Button>(R.id.connectButton).setOnClickListener { connect() }
        findViewById<Button>(R.id.workspaceButton).setOnClickListener { chooseWorkspace() }
        runButton.setOnClickListener { runAgent() }
        applyButton.setOnClickListener { confirmApply() }
    }

    private fun connect() {
        val key = apiKey.text.toString().trim()
        if (key.isBlank()) return toast("API Key پنل AiBus را وارد کنید.")
        busy(true, "● Connecting")
        executor.execute {
            try {
                val loaded = client.models(baseUrl.text.toString(), key)
                runOnUiThread {
                    models = loaded
                    modelSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_item, loaded).apply { setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item) }
                    val codex = loaded.indexOfFirst { it.id.contains("codex", true) }
                    if (codex >= 0) modelSpinner.setSelection(codex)
                    status.text = "● ${loaded.size} Models"; status.setTextColor(getColor(R.color.arka_mint)); output.text = "اتصال برقرار شد. مدل، سطح هوشمندی و Workspace را انتخاب کنید."
                }
            } catch (error: Exception) { runOnUiThread { showError(error.message ?: "خطای اتصال") } }
            finally { runOnUiThread { busy(false) } }
        }
    }

    private fun chooseWorkspace() {
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)
        startActivityForResult(intent, REQUEST_WORKSPACE)
    }

    @Deprecated("Legacy result API keeps this client dependency-free")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST_WORKSPACE || resultCode != RESULT_OK) return
        val uri = data?.data ?: return
        contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        workspaceUri = uri
        workspaceLabel.text = "Workspace فعال\n${uri.lastPathSegment.orEmpty()}"
        output.text = "Workspace با دسترسی خواندن و نوشتن انتخاب شد. تمام تغییرات قبل از اعمال نمایش داده می‌شوند."
    }

    private fun runAgent() {
        val tree = workspaceUri ?: return toast("ابتدا پوشه پروژه را انتخاب کنید.")
        val model = models.getOrNull(modelSpinner.selectedItemPosition) ?: return toast("ابتدا به AiBus متصل شوید و مدل را انتخاب کنید.")
        val key = apiKey.text.toString().trim()
        val task = taskInput.text.toString().trim()
        if (task.length < 3) return toast("دستور Agent را وارد کنید.")
        val effort = when (effortGroup.checkedRadioButtonId) { R.id.effortLow -> "low"; R.id.effortHigh -> "high"; R.id.effortXHigh -> "xhigh"; else -> "medium" }
        busy(true, "● Agent running")
        output.text = "در حال تحلیل فایل‌های Workspace و ساخت برنامه تغییرات..."
        executor.execute {
            try {
                val files = workspace.snapshot(tree)
                val result = client.runAgent(baseUrl.text.toString(), key, model, effort, task, files)
                runOnUiThread {
                    pendingOperations = result.operations
                    applyButton.isEnabled = result.operations.isNotEmpty()
                    output.text = buildString {
                        append(result.summary).append("\n\n").append(result.explanation)
                        if (result.operations.isNotEmpty()) append("\n\n").append(result.operations.joinToString("\n\n") { "${it.action.uppercase()}  ${it.path}\n────────────────\n${it.content.take(1800)}${if (it.content.length > 1800) "\n…" else ""}" })
                        else append("\n\n").append(result.raw)
                    }
                }
            } catch (error: Exception) { runOnUiThread { showError(error.message ?: "اجرای Agent ناموفق بود") } }
            finally { runOnUiThread { busy(false) } }
        }
    }

    private fun confirmApply() {
        val tree = workspaceUri ?: return
        if (pendingOperations.isEmpty()) return
        AlertDialog.Builder(this).setTitle("تأیید تغییرات ArkaCode").setMessage("${pendingOperations.size} فایل تغییر می‌کند. قبل از بازنویسی، نسخه پشتیبان در .arkacode/backups ساخته می‌شود. ادامه می‌دهید؟")
            .setNegativeButton("انصراف", null).setPositiveButton("اعمال تغییرات") { _, _ -> applyChanges(tree) }.show()
    }

    private fun applyChanges(tree: Uri) {
        busy(true, "● Applying")
        executor.execute {
            try {
                val count = workspace.apply(tree, pendingOperations)
                runOnUiThread { pendingOperations = emptyList(); applyButton.isEnabled = false; output.text = "$count فایل با موفقیت اعمال شد. نسخه پشتیبان نیز ساخته شد."; toast("تغییرات اعمال شد") }
            } catch (error: Exception) { runOnUiThread { showError(error.message ?: "اعمال تغییرات ناموفق بود") } }
            finally { runOnUiThread { busy(false) } }
        }
    }

    private fun busy(value: Boolean, label: String = "") {
        runButton.isEnabled = !value; applyButton.isEnabled = !value && pendingOperations.isNotEmpty()
        if (label.isNotBlank()) status.text = label
        if (!value && models.isNotEmpty()) { status.text = "● ${models.size} Models"; status.setTextColor(getColor(R.color.arka_mint)) }
    }

    private fun showError(message: String) { output.text = message; status.text = "● Error"; status.setTextColor(Color.rgb(251, 113, 133)); AlertDialog.Builder(this).setTitle("ArkaCode").setMessage(message).setPositiveButton("باشه", null).show() }
    private fun toast(message: String) { Toast.makeText(this, message, Toast.LENGTH_SHORT).show() }

    override fun onDestroy() { executor.shutdownNow(); super.onDestroy() }
    companion object { private const val REQUEST_WORKSPACE = 1001 }
}
