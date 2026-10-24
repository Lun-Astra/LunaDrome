#!/bin/bash
# Phone layout check (centered Home header + pages, album grid, slide-up player, More menu).
# Nexus 5X, 1080x1920, 420 dpi = 2.625 px/dp. The fake Navidrome listens on the runner (10.0.2.2).
# Bottom tabs (y 1734): Home 90, Search 270, Artists 450, Albums 630, Tracks 810, More 990.
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
adb shell input swipe 540 1400 540 700 600; sleep 3; shot 03-home-scrolled
tap 630 1734; sleep 12; anr; shot 04-albums
tap 810 1734; sleep 12; anr; shot 05-tracks
tap 60 127; sleep 3; anr; tap 113 252; sleep 10; anr; shot 06-playing   # play-all menu > Play
tap 400 1570; sleep 0.15; shot 07-player-opening; sleep 3; shot 08-player
adb shell input keyevent 4; sleep 3; shot 09-player-closed
tap 990 1734; sleep 0.15; shot 10-more-opening; sleep 3; shot 11-more
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | head -50 > $OUT/logcat-errors.txt
ls -la $OUT
