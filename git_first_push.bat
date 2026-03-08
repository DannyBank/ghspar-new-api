@echo off

echo Initializing git repository...
git init

echo Adding files...
git add .

echo Creating commit...
git commit -m "Auto commit"

echo Adding remote repository...
set /p repo=Enter repository URL: 
git remote add origin %repo%

echo Pushing to repository...
git branch -M main
git push -u origin main

echo Done!
pause