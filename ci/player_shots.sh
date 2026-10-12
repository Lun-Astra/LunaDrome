#!/bin/bash
# #29: screenshots of the phone player (mini bar + full-screen player) in an emulator.
# Nexus 5X, 1080x1920, 420 dpi = 2.625 px/dp. The fake Navidrome listens on the runner (10.0.2.2).
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
APK=$(ls publish/android/*-Signed.apk | head -1)
adb shell settings put global hide_error_dialogs 1
sleep 60
adb install -r "$APK"
for ime in $(adb shell ime list -s); do adb shell ime disable "$ime"; done
adb logcat -c
adb shell monkey -p com.lunastra.lunadrome -c android.intent.category.LAUNCHER 1
sleep 60; anr; sleep 20; anr; shot 01-launch
tap 540 664; sleep 2; adb shell input keycombination 113 29; adb shell input text "http://10.0.2.2:4533"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
tap 540 1260
sleep 25; anr; shot 02-home
tap 811 1734; sleep 15; anr; shot 03-tracks
row() { echo $(( (164 + ($1 - 1) * 56) * 21 / 8 )); }
tap 60 127; sleep 3; anr; tap 113 252; sleep 10; anr; shot 04-playing-list   # play-all menu > Play
tap 400 1570; sleep 6; anr; shot 05-player                   # the full-screen player
sleep 8; shot 06-player-later
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | head -50 > $OUT/logcat-errors.txt
ls -la $OUT
