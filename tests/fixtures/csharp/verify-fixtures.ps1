param(
    [string]$FixtureRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

function Assert-Line {
    param(
        [string]$Path,
        [int]$Number,
        [string]$Expected
    )

    $lines = [System.IO.File]::ReadAllLines($Path, [System.Text.UTF8Encoding]::new($false))
    if ($lines[$Number - 1] -cne $Expected) {
        throw "Unexpected content at $Path`:$Number"
    }
}

function Assert-FileExists {
    param([string]$Path)

    if (-not [System.IO.File]::Exists($Path)) {
        throw "Missing fixture file: $Path"
    }
}

$navigation = Join-Path $FixtureRoot 'navigation'
$encoding = Join-Path $FixtureRoot 'encoding'
$diff = Join-Path $FixtureRoot 'diff'

@(
    (Join-Path $navigation 'Fixture.Navigation.csproj'),
    (Join-Path $navigation 'Fixture.LinkedSecond.csproj'),
    (Join-Path $navigation 'RefKinds.cs'),
    (Join-Path $navigation 'Contracts.cs'),
    (Join-Path $navigation 'Catalog.Partial.cs'),
    (Join-Path $navigation 'CallSites.cs'),
    (Join-Path $FixtureRoot 'shared\LinkedHelper.cs'),
    (Join-Path $navigation 'DynamicCandidates.cs'),
    (Join-Path $navigation 'expected.md'),
    (Join-Path $encoding 'CrLfEmoji.cs'),
    (Join-Path $encoding 'expected.md'),
    (Join-Path $diff 'expected.md')
) | ForEach-Object { Assert-FileExists $_ }

Assert-Line (Join-Path $navigation 'Contracts.cs') 8 'public partial class Catalog : ILoader'
Assert-Line (Join-Path $navigation 'Contracts.cs') 10 '    /// <summary>Loads one value.</summary>'
Assert-Line (Join-Path $navigation 'Contracts.cs') 11 '    // This text is resolved lazily from the verified current source.'
Assert-Line (Join-Path $navigation 'Contracts.cs') 12 '    public void Load(string value)'
Assert-Line (Join-Path $navigation 'Contracts.cs') 16 '    public void Load(string value, int retryCount)'
Assert-Line (Join-Path $navigation 'Contracts.cs') 20 '    private T Load<T>(T value)'
Assert-Line (Join-Path $navigation 'Contracts.cs') 25 '    void ILoader.Load(string value)'
Assert-Line (Join-Path $navigation 'Catalog.Partial.cs') 3 'public partial class Catalog'
Assert-Line (Join-Path $navigation 'RefKinds.cs') 5 '    internal void Mutate(int value) { }'
Assert-Line (Join-Path $navigation 'RefKinds.cs') 6 '    protected void Mutate(ref int value) { }'
Assert-Line (Join-Path $navigation 'CallSites.cs') 7 '        catalog.Load("one");'
Assert-Line (Join-Path $navigation 'CallSites.cs') 8 '        catalog.Load("two", 2);'
Assert-Line (Join-Path $navigation 'CallSites.cs') 14 '        return Use(new Catalog());'
Assert-Line (Join-Path $navigation 'CallSites.cs') 19 '        loader.Load("interface");'
Assert-Line (Join-Path $navigation 'DynamicCandidates.cs') 22 '        return Activator.CreateInstance(typeof(Worker))!;'
Assert-Line (Join-Path $navigation 'DynamicCandidates.cs') 25 '    public static string ServiceKey => "Fixture.Navigation.IWorker";'
Assert-Line (Join-Path $navigation 'DynamicCandidates.cs') 27 '    public static string MemberKey => "Run";'
Assert-Line (Join-Path $navigation 'DynamicCandidates.cs') 31 '        return Type.GetType("Fixture.Navigation.Worker");'

$crlfPath = Join-Path $encoding 'CrLfEmoji.cs'
$bytes = [System.IO.File]::ReadAllBytes($crlfPath)
for ($index = 0; $index -lt $bytes.Length; $index++) {
    if ($bytes[$index] -eq 10 -and ($index -eq 0 -or $bytes[$index - 1] -ne 13)) {
        throw 'CrLfEmoji.cs must use CRLF line endings.'
    }
}
if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) {
    throw 'CrLfEmoji.cs must not contain a UTF-8 BOM.'
}
Assert-Line $crlfPath 5 '    public const string Greeting = "안녕 👋";'

$emojiLine = [System.IO.File]::ReadAllLines($crlfPath, [System.Text.UTF8Encoding]::new($false))[4]
if ($emojiLine.IndexOf([char]0xD83D) -ne 39 -or $emojiLine.IndexOf([char]0xDC4B) -ne 40) {
    throw 'Emoji UTF-16 position drifted from columns 40-41.'
}

Assert-Line (Join-Path $diff 'vcs-base\ReviewTarget.cs') 7 '        return "base";'
Assert-Line (Join-Path $diff 'vcs-target\ReviewTarget.cs') 7 '        return "dirty-before-session";'
Assert-Line (Join-Path $diff 'session-start\ReviewTarget.cs') 7 '        return "dirty-before-session";'
Assert-Line (Join-Path $diff 'session-current\ReviewTarget.cs') 7 '        return "dirty-before-session";'
Assert-Line (Join-Path $diff 'vcs-target\ReviewTarget.cs') 10 '    public static string AddedDuringSession()'
Assert-Line (Join-Path $diff 'session-current\ReviewTarget.cs') 10 '    public static string AddedDuringSession()'

Write-Output 'C# fixture integrity check passed.'

