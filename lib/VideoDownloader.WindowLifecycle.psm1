#requires -Version 7.0

function Start-VdGuiMessageLoop {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Windows.Window]$Window
    )

    # Unlike ShowDialog(), Application.Run() keeps the message loop alive when
    # the main window is hidden in the tray. Closing the window exits the app.
    $app = [System.Windows.Application]::new()
    $app.ShutdownMode = [System.Windows.ShutdownMode]::OnMainWindowClose
    [void]$app.Run($Window)
}

Export-ModuleMember -Function Start-VdGuiMessageLoop
