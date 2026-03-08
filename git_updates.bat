@echo off
git add .
set /p msg=Enter commit message: 
git commit -m "%msg%"
git push
pause