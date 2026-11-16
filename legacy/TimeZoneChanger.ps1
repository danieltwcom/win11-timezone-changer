<#
    TimeZoneChanger.ps1
    A small Windows 11 GUI tool that shows the current system time zone
    and lets the user search for and apply a new one.
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# ---------- Helpers ----------
function Get-CurrentTz {
    [System.TimeZoneInfo]::ClearCachedData()   # make sure we read the live system value
    return [System.TimeZoneInfo]::Local
}

function Format-Offset([TimeSpan]$offset) {
    $sign = if ($offset -lt [TimeSpan]::Zero) { '-' } else { '+' }
    return 'UTC{0}{1:hh\:mm}' -f $sign, $offset.Duration()
}

$allZones = [System.TimeZoneInfo]::GetSystemTimeZones() | Sort-Object BaseUtcOffset, DisplayName

# ---------- Form ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Windows Time Zone Changer'
$form.Size = New-Object System.Drawing.Size(620, 600)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false

# --- Current time zone group ---
$grpCurrent = New-Object System.Windows.Forms.GroupBox
$grpCurrent.Text = 'Current System Time Zone'
$grpCurrent.Location = New-Object System.Drawing.Point(12, 10)
$grpCurrent.Size = New-Object System.Drawing.Size(580, 150)
$form.Controls.Add($grpCurrent)

$lblName = New-Object System.Windows.Forms.Label
$lblName.Location = New-Object System.Drawing.Point(12, 25)
$lblName.Size = New-Object System.Drawing.Size(555, 25)
$lblName.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
$grpCurrent.Controls.Add($lblName)

$lblDetails = New-Object System.Windows.Forms.Label
$lblDetails.Location = New-Object System.Drawing.Point(12, 52)
$lblDetails.Size = New-Object System.Drawing.Size(555, 50)
$grpCurrent.Controls.Add($lblDetails)

$lblClock = New-Object System.Windows.Forms.Label
$lblClock.Location = New-Object System.Drawing.Point(12, 105)
$lblClock.Size = New-Object System.Drawing.Size(555, 32)
$lblClock.Font = New-Object System.Drawing.Font('Segoe UI', 14)
$lblClock.ForeColor = [System.Drawing.Color]::DarkBlue
$grpCurrent.Controls.Add($lblClock)

# --- Change time zone group ---
$grpChange = New-Object System.Windows.Forms.GroupBox
$grpChange.Text = 'Change Time Zone'
$grpChange.Location = New-Object System.Drawing.Point(12, 170)
$grpChange.Size = New-Object System.Drawing.Size(580, 375)
$form.Controls.Add($grpChange)

$lblSearch = New-Object System.Windows.Forms.Label
$lblSearch.Text = 'Search:'
$lblSearch.Location = New-Object System.Drawing.Point(12, 30)
$lblSearch.AutoSize = $true
$grpChange.Controls.Add($lblSearch)

$txtSearch = New-Object System.Windows.Forms.TextBox
$txtSearch.Location = New-Object System.Drawing.Point(75, 27)
$txtSearch.Size = New-Object System.Drawing.Size(490, 25)
$grpChange.Controls.Add($txtSearch)

$lstZones = New-Object System.Windows.Forms.ListBox
$lstZones.Location = New-Object System.Drawing.Point(12, 60)
$lstZones.Size = New-Object System.Drawing.Size(553, 230)
$lstZones.DisplayMember = 'DisplayName'
$lstZones.HorizontalScrollbar = $true
$grpChange.Controls.Add($lstZones)

$lblPreview = New-Object System.Windows.Forms.Label
$lblPreview.Location = New-Object System.Drawing.Point(12, 295)
$lblPreview.Size = New-Object System.Drawing.Size(553, 25)
$lblPreview.ForeColor = [System.Drawing.Color]::DimGray
$grpChange.Controls.Add($lblPreview)

$btnApply = New-Object System.Windows.Forms.Button
$btnApply.Text = 'Apply Selected Time Zone'
$btnApply.Location = New-Object System.Drawing.Point(12, 325)
$btnApply.Size = New-Object System.Drawing.Size(220, 35)
$btnApply.Enabled = $false
$grpChange.Controls.Add($btnApply)

$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Text = 'Refresh'
$btnRefresh.Location = New-Object System.Drawing.Point(240, 325)
$btnRefresh.Size = New-Object System.Drawing.Size(100, 35)
$grpChange.Controls.Add($btnRefresh)

$btnSettings = New-Object System.Windows.Forms.Button
$btnSettings.Text = 'Open Windows Settings'
$btnSettings.Location = New-Object System.Drawing.Point(375, 325)
$btnSettings.Size = New-Object System.Drawing.Size(190, 35)
$grpChange.Controls.Add($btnSettings)

# ---------- Logic ----------
function Update-CurrentInfo {
    $tz  = Get-CurrentTz
    $now = [DateTime]::Now
    $isDst = $tz.IsDaylightSavingTime($now)
    $script:currentTz = $tz

    $lblName.Text = $tz.DisplayName
    $lblDetails.Text = "ID: $($tz.Id)`r`n" +
        "Current offset: $(Format-Offset ($tz.GetUtcOffset($now)))   |   " +
        "Daylight saving: $(if ($tz.SupportsDaylightSavingTime) { if ($isDst) { 'Yes (active)' } else { 'Supported (not active)' } } else { 'Not observed' })"
}

function Update-Clock {
    $now = [DateTime]::Now
    $lblClock.Text = 'Local time:  ' + $now.ToString('yyyy-MM-dd  HH:mm:ss  (dddd)')
    $sel = $lstZones.SelectedItem
    if ($sel) {
        $t = [System.TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, $sel.Id)
        $lblPreview.Text = "Time in selected zone: $($t.ToString('yyyy-MM-dd HH:mm:ss'))  ($(Format-Offset ($sel.GetUtcOffset([DateTime]::UtcNow))))"
    } else {
        $lblPreview.Text = ''
    }
}

function Update-ZoneList {
    $filter = $txtSearch.Text.Trim()
    $lstZones.BeginUpdate()
    $lstZones.Items.Clear()
    foreach ($z in $allZones) {
        if ($filter -eq '' -or
            $z.DisplayName -like "*$filter*" -or
            $z.Id -like "*$filter*" -or
            $z.StandardName -like "*$filter*") {
            [void]$lstZones.Items.Add($z)
            if ($script:currentTz -and $z.Id -eq $script:currentTz.Id -and $filter -eq '') {
                $lstZones.SelectedItem = $z
            }
        }
    }
    $lstZones.EndUpdate()
}

$txtSearch.Add_TextChanged({ Update-ZoneList })

$lstZones.Add_SelectedIndexChanged({
    $sel = $lstZones.SelectedItem
    $btnApply.Enabled = ($null -ne $sel -and $sel.Id -ne $script:currentTz.Id)
    Update-Clock
})

$lstZones.Add_DoubleClick({ if ($btnApply.Enabled) { $btnApply.PerformClick() } })

$btnApply.Add_Click({
    $sel = $lstZones.SelectedItem
    if (-not $sel) { return }

    $answer = [System.Windows.Forms.MessageBox]::Show(
        "Change system time zone to:`r`n`r`n$($sel.DisplayName)?",
        'Confirm', 'YesNo', 'Question')
    if ($answer -ne 'Yes') { return }

    try {
        Set-TimeZone -Id $sel.Id -ErrorAction Stop
    } catch {
        # Fallback to tzutil if Set-TimeZone is unavailable or fails
        & tzutil.exe /s "$($sel.Id)" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            [System.Windows.Forms.MessageBox]::Show(
                "Failed to change time zone.`r`n`r`n$($_.Exception.Message)`r`n`r`nTry running this program as Administrator.",
                'Error', 'OK', 'Error') | Out-Null
            return
        }
    }

    Update-CurrentInfo
    $btnApply.Enabled = $false
    Update-Clock
    [System.Windows.Forms.MessageBox]::Show(
        "Time zone changed to:`r`n$($script:currentTz.DisplayName)",
        'Success', 'OK', 'Information') | Out-Null
})

$btnRefresh.Add_Click({
    Update-CurrentInfo
    $txtSearch.Text = ''
    Update-ZoneList
    Update-Clock
})

$btnSettings.Add_Click({ Start-Process 'ms-settings:dateandtime' })

# Live clock timer
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({ Update-Clock })

$form.Add_Shown({
    Update-CurrentInfo
    Update-ZoneList
    Update-Clock
    $timer.Start()
    $txtSearch.Focus()
})
$form.Add_FormClosed({ $timer.Stop(); $timer.Dispose() })

[void]$form.ShowDialog()
