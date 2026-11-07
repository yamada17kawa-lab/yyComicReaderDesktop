@echo off
chcp 65001 >nul
if exist "%~dp0publish\YY漫画阅读器.exe" (
    copy /Y "%~dp0publish\YY漫画阅读器.exe" "%~dp0YY漫画阅读器.exe" >nul 2>&1
)
if exist "%~dp0YY漫画阅读器.exe" (
    start "" "%~dp0YY漫画阅读器.exe"
) else if exist "%~dp0publish\YY漫画阅读器.exe" (
    start "" "%~dp0publish\YY漫画阅读器.exe"
)
exit
