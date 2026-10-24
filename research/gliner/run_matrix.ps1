param([string]$Label=('gliner-scope-'+(Get-Date -Format yyyyMMdd-HHmmss)), [string]$Corpus='tests/nlp-extended-cases.json', [ValidateSet('compact','wide')][string]$SchemaFlavor='wide')
$ErrorActionPreference='Stop'
$pcRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $pcRoot
if($Label -notmatch '^[A-Za-z0-9_-]+$'){throw 'Use a plain experiment label'}
$pcOutput=Join-Path (Join-Path $pcRoot 'artifacts') $Label
if(Test-Path -LiteralPath $pcOutput){throw 'Experiment directory must be new'}
[IO.Directory]::CreateDirectory($pcOutput) | Out-Null
$pcPython=Join-Path $pcRoot '.tools/nlp/gliner-venv/Scripts/python.exe'
$pcModel=Join-Path $pcRoot '.tools/nlp/gliner2.5-multi-cf5593a'
$pcProbe=Join-Path $PSScriptRoot 'probe.py'
$pcCorpus=[IO.Path]::GetFullPath((Join-Path $pcRoot $Corpus))
$pcProtocol=[ordered]@{Format='gliner-matrix-1.1';SchemaFlavor=$SchemaFlavor;Started=[DateTimeOffset]::Now.ToUniversalTime().ToString('O');WrapperSha256=(Get-FileHash $PSCommandPath).Hash;ProbeSha256=(Get-FileHash $pcProbe).Hash;CorpusSha256=(Get-FileHash $pcCorpus).Hash;Threshold=0.5;Threads=4;Seed=42;Completed=$false;Notice='Four predeclared variants. No mid-run semantic tuning, no product settings or user history. Shapes and offsets are not correctness scores.';Runs=@()}
function SaveProtocol {$pcProtocol | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $pcOutput 'matrix.json') -Encoding UTF8}
SaveProtocol
foreach($pcStrategy in @('records','attributes')){
 foreach($pcSplitter in @('char','whitespace')){
  $pcVariant=$pcStrategy+'-'+$pcSplitter
  $pcSchema=Join-Path $PSScriptRoot $(if($SchemaFlavor -eq 'wide'){'schema-wide-'+$pcStrategy+'-v1.json'}elseif($pcStrategy -eq 'records'){'schema-v1.json'}else{'schema-attributes-v1.json'})
  $pcVariantOutput=Join-Path $pcOutput $pcVariant
  $pcArgs=@(('"'+$pcProbe+'"'),'--model',('"'+$pcModel+'"'),'--corpus',('"'+$pcCorpus+'"'),'--schema',('"'+$pcSchema+'"'),'--strategy',$pcStrategy,'--splitter',$pcSplitter,'--output',('"'+$pcVariantOutput+'"'))
  $pcProcess=Start-Process -FilePath $pcPython -ArgumentList $pcArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $pcOutput ($pcVariant+'.stdout.log')) -RedirectStandardError (Join-Path $pcOutput ($pcVariant+'.stderr.log'))
  $pcHandle=$pcProcess.Handle
  $pcRun=[ordered]@{Variant=$pcVariant;PID=$pcProcess.Id;Started=$pcProcess.StartTime.ToUniversalTime().ToString('O');SchemaSha256=(Get-FileHash $pcSchema).Hash;Completed=$false;ExitCode=$null}
  $pcProtocol.Runs+=,$pcRun;SaveProtocol
  if(-not $pcProcess.WaitForExit(300000)){throw ('Observation expired; inspect same PID '+$pcProcess.Id)}
  $pcProcess.WaitForExit();$pcProcess.Refresh()
  $pcRun.Completed=$true;$pcRun.ExitCode=$pcProcess.ExitCode;SaveProtocol
  if($pcRun.ExitCode -ne 0){throw ('Variant failed '+$pcVariant)}
  if((Get-FileHash $pcProbe).Hash -ne $pcProtocol.ProbeSha256 -or (Get-FileHash $pcCorpus).Hash -ne $pcProtocol.CorpusSha256 -or (Get-FileHash $pcSchema).Hash -ne $pcRun.SchemaSha256){throw 'Experiment inputs changed'}
  Write-Output ($pcVariant+': actual exit '+$pcRun.ExitCode)
 }
}
$pcProtocol.Completed=$true;SaveProtocol
Write-Output $pcOutput
exit 0
