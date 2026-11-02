#!/bin/bash
# #28: offline downloads on a phone (emulator, fake Navidrome on the runner = 10.0.2.2).
# Download a song from the selection menu, open Downloaded, then airplane mode + restart:
# "Play downloaded songs" on the login screen, and the song plays from the device.
# Nexus 5X, 1080x1920, 420 dpi = 2.625 px/dp.
set -x
OUT=ui-test-out; mkdir -p $OUT
shot() { adb exec-out screencap -p > "$OUT/$1.png"; }
anr() {
  for i in 1 2 3; do
    if adb shell dumpsys window | grep -qi "Application Not Responding"; then
      adb shell input tap 300 1084; sleep 5
    else return; fi
  done
}
tap() { anr; adb shell input tap "$@"; }
row() { echo $(( (164 + ($1 - 1) * 56) * 21 / 8 )); }
# Tap the element whose text or description contains $1 (uiautomator); falls back to $2 $3.
tapText() {
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1
  adb pull /sdcard/ui.xml "$OUT/ui-$4.xml" >/dev/null 2>&1
  local b=$(grep -o "<node[^>]*\(text\|content-desc\)=\"[^\"]*$1[^\"]*\"[^>]*>" "$OUT/ui-$4.xml" | head -1 | grep -o 'bounds="[^"]*"' | grep -o '[0-9]\+' | tr '\n' ' ')
  if [ -n "$b" ]; then
    set -- $b
    echo "tapText found at $1,$2-$3,$4"
    tap $(( ($1 + $3) / 2 )) $(( ($2 + $4) / 2 ))
  else
    echo "tapText: '$1' not found, tapping $2,$3"
    tap "$2" "$3"
  fi
}
APK=$(ls publish/android/*-Signed.apk | head -1)
adb shell settings put global hide_error_dialogs 1
sleep 60
adb install -r "$APK"
for ime in $(adb shell ime list -s); do adb shell ime disable "$ime"; done
adb logcat -c
adb shell monkey -p com.lunastra.lunadrome -c android.intent.category.LAUNCHER 1
sleep 60; anr; sleep 20; anr
tap 540 664; sleep 2; adb shell input keycombination 113 29; adb shell input text "http://10.0.2.2:4533"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
tap 540 1260
sleep 25; anr
tap 810 1734; sleep 12; anr; shot d01-tracks
# Hold row 2 -> selecting; its ... menu -> Download for offline
adb shell input swipe 500 $(row 2) 500 $(row 2) 900; sleep 2
tap 990 150; sleep 2; shot d02-menu
tapText "Download for offline" 400 1232 menu; sleep 1; shot d03-after-download
sleep 15
tap 90 153; sleep 2   # end selecting
# More -> Downloaded
tap 990 1734; sleep 3; shot d04-more
tapText "Downloaded" 210 1063 more; sleep 4; shot d05-downloaded
# Offline: airplane mode, restart the app
adb shell cmd connectivity airplane-mode enable; sleep 5
adb shell am force-stop com.lunastra.lunadrome; sleep 2
adb shell monkey -p com.lunastra.lunadrome -c android.intent.category.LAUNCHER 1
sleep 45; anr; shot d06-login-offline
tapText "Play downloaded songs" 540 1435 login; sleep 5; shot d07-offline-page
# Play the first downloaded song (its row) and check the log says "downloaded"
tap 500 124; sleep 8; shot d08-playing   # the page's Play button
sleep 10; shot d08b-still-playing
tap 990 1734; sleep 3; shot d09-offline-more
adb shell cmd connectivity airplane-mode disable
adb logcat -d -v time > $OUT/logcat-all.txt
adb shell run-as com.lunastra.lunadrome find . -name playback.log 2>/dev/null | head -1 | while read f; do adb shell run-as com.lunastra.lunadrome cat "$f" > $OUT/playback.log; done
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | grep -v "RuntimeInit\|Monkey\|boot image\|lock profiling\|VM exiting" | head -50 > $OUT/logcat-errors.txt
ls -la $OUT
