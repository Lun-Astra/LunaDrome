#!/bin/bash
# Menus, swipe-to-queue feedback and touch scrolling on a phone (emulator, fake Navidrome).
# Nexus 5X, 1080x1920, 420 dpi = 2.625 px/dp. Bottom tabs (y 1734): Home 90, Search 270,
# Albums 450, Artists 630, Tracks 810, More 990. Track rows: row n at y (164+(n-1)*56)*21/8.
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
sleep 25; anr; shot 01-home
# Tracks, play everything (so the bar has a song)
tap 810 1734; sleep 12; anr
tap 60 127; sleep 3; anr; shot 02-play-menu; tap 113 252; sleep 10; anr; shot 03-playing
# Swipe row 3 right in one quick motion -> "Added to queue" (and a vibration on a phone)
adb shell input swipe 200 $(row 3) 700 $(row 3) 250; sleep 0.4; shot 04-swiped-toast; sleep 3
# Quick swipe down on the playback bar -> Play on menu
adb shell input swipe 400 1560 400 1740 200; sleep 2; shot 05-play-on-menu
adb shell input keyevent 4; sleep 2
# Long-press a row -> selection, then its ... menu (top right of the selection bar)
adb shell input swipe 500 $(row 2) 500 $(row 2) 900; sleep 2; shot 06-selecting
tap 990 150; sleep 2; shot 07-selection-menu
adb shell input keyevent 4; sleep 1; adb shell input keyevent 4; sleep 2
# Albums: open the first album, scroll its tracks up and down: nothing may stay lit or get selected
tap 450 1734; sleep 12; anr
tap 220 420; sleep 10; anr; shot 08-album
adb shell input swipe 540 1400 540 700 500; sleep 1; adb shell input swipe 540 700 540 1400 500; sleep 2; shot 09-album-after-scroll
adb shell input swipe 540 1500 540 900 400; sleep 1; adb shell input swipe 540 900 540 1500 400; sleep 2; shot 10-album-after-scroll-2
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | grep -v "RuntimeInit\|Monkey\|boot image\|lock profiling\|VM exiting" | head -50 > $OUT/logcat-errors.txt
ls -la $OUT
