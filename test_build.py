import subprocess
import sys
result = subprocess.run(
    [sys.executable, '-c', 'import subprocess; r = subprocess.run(["dotnet", "build", "-v", "q", "-nologo"], cwd="F:/porj/sipsoftphone", capture_output=True, text=True, timeout=120); 
     print("EXIT:", r.returncode); 
     print("STDOUT:", r.stdout[-500:] if r.stdout else ""); 
     print("STDERR:", r.stderr[-500:] if r.stderr else "")'],
    capture_output=True, text=True, timeout=130
)
print("PYEXIT:", result.returncode)
print("PYSTDOUT:", result.stdout[-300:] if result.stdout else "")
print("PYSTDERR:", result.stderr[-300:] if result.stderr else "")