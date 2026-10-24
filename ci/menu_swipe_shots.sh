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
# Swipe row 3 right and hold mid-way (tint), then past the threshold, then let go
Y=$(row 3)
adb shell input motionevent DOWN 200 $Y; sleep 0.1
for x in 230 260 290 320; do adb shell input motionevent MOVE $x $Y; sleep 0.05; done
shot 04-swipe-half
for x in 360 400 440; do adb shell input motionevent MOVE $x $Y; sleep 0.05; done
shot 05-swipe-past
adb shell input motionevent UP 440 $Y; sleep 0.15; shot 06-swipe-released; sleep 2; shot 07-swipe-settled
# Swipe down on the playback bar -> Play on menu
adb shell input motionevent DOWN 400 1560; sleep 0.1
for y in 1580 1600 1620 1640 1660; do adb shell input motionevent MOVE 400 $y; sleep 0.05; done
shot 08-bar-pulled
adb shell input motionevent UP 400 1660; sleep 2; shot 09-play-on-menu
adb shell input keyevent 4; sleep 2
# Long-press a row -> selection, then its ... menu
adb shell input swipe 500 $(row 2) 500 $(row 2) 900; sleep 2; shot 10-selecting
tap 990 210; sleep 2; shot 11-selection-menu
adb shell input keyevent 4; sleep 1; adb shell input keyevent 4; sleep 2
# Albums: open the first album, scroll its tracks up and down, nothing may stay lit
tap 450 1734; sleep 12; anr; shot 12-albums
tap 220 420; sleep 10; anr; shot 13-album
adb shell input swipe 540 1400 540 700 500; sleep 1; adb shell input swipe 540 700 540 1400 500; sleep 2; shot 14-album-after-scroll
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | grep -v "RuntimeInit\|Monkey\|boot image\|lock profiling\|VM exiting" | head -50 > $OUT/logcat-errors.txt
ls -la $OUT
