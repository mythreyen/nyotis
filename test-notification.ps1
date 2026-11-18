param(
    [string]$Title = "Antigravity Assistant",
    [string]$Message = "Notification delivered to DynamicPill! 🚀",
    [string]$AppName = "Antigravity"
)

# 1. Send directly to DynamicPill Named Pipe overlay
$deliveredViaPipe = $false
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "DynamicPill_Pipe", [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect(350)
    $writer = New-Object System.IO.StreamWriter($pipe, [System.Text.Encoding]::UTF8)
    $writer.WriteLine("$Title|$Message|$AppName")
    $writer.Flush()
    $pipe.Dispose()
    $deliveredViaPipe = $true
    Write-Host "✅ Notification sent directly to DynamicPill overlay!"
} catch {
    Write-Host "ℹ DynamicPill pipe not connected, falling back to Windows toast."
}

# 2. Also send Windows Action Center Toast Notification
try {
    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null

    $escapedTitle = [System.Security.SecurityElement]::Escape($Title)
    $escapedMessage = [System.Security.SecurityElement]::Escape($Message)

    $template = @"
<toast>
  <visual>
    <binding template="ToastGeneric">
      <text>$escapedTitle</text>
      <text>$escapedMessage</text>
    </binding>
  </visual>
</toast>
"@

    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $xml.LoadXml($template)
    $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("DynamicPill.Test").Show($toast)
    Write-Host "✅ Windows toast notification dispatched: '$Title' - '$Message'"
} catch {
    Write-Host "Windows toast error: $_"
}
