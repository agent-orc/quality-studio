from pathlib import Path
import os, json, tempfile, shutil, subprocess, socket, urllib.request, urllib.error, time, hashlib
repo=Path("C:/Projects/quality-studio")
out=repo/"results/review-2026-09-19/sensor-report-audit"
out.mkdir(exist_ok=True)
temporary=Path(tempfile.mkdtemp(prefix="qs-sensor-report-probes-")).resolve()
temp_parent=Path(tempfile.gettempdir()).resolve()
assert temporary.parent==temp_parent and temporary.name.startswith("qs-sensor-report-probes-")
process=None
results={"runtime":"copied existing Release API; no build", "cases":[]}
try:
    copied=temporary/"api-bin"
    shutil.copytree(repo/"backend/QualityStudio.Api/bin/Release/net10.0",copied)
    dll=copied/"AgentOrchestrator.CodeQuality.dll"
    results["coreDllSha256"]=hashlib.sha256(dll.read_bytes()).hexdigest()
    host=temporary/"host"; host.mkdir()
    fixture=temporary/"repository"; fixture.mkdir()
    (fixture/"example.cs").write_text("class Example {}")
    subprocess.run(["git","init","--quiet",str(fixture)],check=True)
    registry=host/".quality-studio"; registry.mkdir()
    entry={"id":"default","displayName":"Isolated sensor fixture","rootPath":str(fixture),"inputBudgetCharacters":20000,"enabledReviewKinds":["code"],"sensors":[{"id":sensor,"enabled":True,"configuration":{"reportPath":"probe.sarif"}} for sensor in ["sarif","roslyn","eslint"]]+[{"id":"coverage","enabled":True,"configuration":{"reportPaths":"probe.xml"}}]}
    (registry/"repositories.json").write_text(json.dumps([entry]))
    with socket.socket() as listener:
        listener.bind(("127.0.0.1",0)); port=listener.getsockname()[1]
    env=dict(os.environ,QualityStudio__RepositoryRoot=str(fixture),QualityStudio__AllowedRoots__0=str(temporary),QualityStudio__Security__Mode="Local",QUALITY_STUDIO_DATA_ROOT=str(temporary/"data"),QualityStudio__DataRoot=str(temporary/"data"))
    logfile=(out/"api-probe.log").open("w",encoding="utf-8")
    process=subprocess.Popen(["dotnet",str(copied/"QualityStudio.Api.dll"),"--urls",f"http://127.0.0.1:{port}","--contentRoot",str(host)],cwd=host,env=env,stdout=logfile,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
    base=f"http://127.0.0.1:{port}"
    for attempt in range(100):
        try:
            urllib.request.urlopen(base+"/health",timeout=1).read(); break
        except (urllib.error.URLError,TimeoutError):
            if process.poll() is not None: raise RuntimeError("Isolated API exited; inspect log")
            time.sleep(.1)
    else: raise RuntimeError("Isolated API health timed out")
    def scan(label,sensor):
        request=urllib.request.Request(base+"/api/repos/default/sensors/"+sensor+"/scan",method="POST",data=b"")
        with urllib.request.urlopen(request,timeout=10) as response: data=json.load(response)
        results["cases"].append({"case":label,"sensor":sensor,"available":data["available"],"findingCount":len(data["findings"]),"reason":data.get("unavailableReason")})
    tool={"driver":{"name":"fixture"}}
    cases=[
      ("missing",None),
      ("bare-object",{}),
      ("empty-results",{"version":"2.1.0","runs":[{"tool":tool,"results":[]}]}),
      ("results-object",{"version":"2.1.0","runs":[{"tool":tool,"results":{}}]}),
      ("failed-invocation",{"version":"2.1.0","runs":[{"tool":tool,"results":[],"invocations":[{"executionSuccessful":False,"exitCode":2}]}]}),
      ("metadata-only",{"version":"2.1.0","runs":[{"tool":tool}]}),
      ("external-results",{"version":"2.1.0","runs":[{"tool":tool,"results":[],"externalPropertyFileReferences":{"results":[{"location":{"uri":"external.sarif"},"itemCount":1}]}}]}),
      ("no-runs",{"version":"2.1.0","runs":[]})
    ]
    for label,payload in cases:
        report=fixture/"probe.sarif"
        if payload is None:
            report.unlink(missing_ok=True)
        else: report.write_text(json.dumps(payload))
        scan(label,"sarif")
    (fixture/"probe.sarif").write_text(json.dumps(cases[3][1]))
    for sensor in ["roslyn","eslint"]: scan("results-object",sensor)
    for label,payload in [("missing",None),("unknown-xml","<unrelated/>"),("empty-cobertura","<coverage/>"),("known-cobertura",'<coverage><packages><package><classes><class filename="example.cs"><lines><line number="1" hits="1"/></lines></class></classes></package></packages></coverage>')]:
        report=fixture/"probe.xml"
        if payload is None: report.unlink(missing_ok=True)
        else: report.write_text(payload)
        scan(label,"coverage")
finally:
    if process is not None and process.poll() is None:
        subprocess.run(["taskkill","/PID",str(process.pid),"/T","/F"],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        if process.poll() is None: process.terminate()
        try: process.wait(timeout=8)
        except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
    if "logfile" in globals(): logfile.close()
    assert temporary.parent==temp_parent and temporary.name.startswith("qs-sensor-report-probes-")
    def clear_readonly(function, path, exc):
        os.chmod(path, 0o700)
        function(path)
    shutil.rmtree(temporary,onexc=clear_readonly)
    results["cleanup"]="Copied API process stopped; verified own temporary directory removed."
    (out/"probe-results.json").write_text(json.dumps(results,indent=2)+"\n",encoding="utf-8")
    print(json.dumps(results,indent=2))
