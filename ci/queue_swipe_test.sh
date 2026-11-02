#!/bin/bash
# #37: swipe-to-remove in the phone player's queue. A short left swipe must not remove a song
# any more; a long one removes it on release (slide-out) and the toast's Undo puts it back.
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
tap 811 1734; sleep 15; anr
tap 60 127; sleep 3; anr; tap 113 252; sleep 10; anr    # Tracks: play-all menu > Play
tap 400 1570; sleep 6; anr; shot q0-player              # the phone player
tap 976 136; sleep 4; anr; shot q1-queue                 # its Queue button (top right)
# q2: a short left swipe (~95 dp, under the 120 dp) - nothing may be removed
adb shell input swipe 800 800 550 800 250; sleep 2; shot q2-short-swipe
# q3: a long left swipe (~230 dp) - removed on release, toast with Undo
adb shell input swipe 900 800 300 800 450; sleep 0.25; shot q3a-sliding-out; sleep 1; shot q3-long-swipe
# q4: a quick long flick still removes
adb shell input swipe 900 1000 250 1000 120; sleep 1.5; shot q4-flick
# q5: vertical scroll of the queue - nothing removed
adb shell input swipe 540 1300 540 700 400; sleep 2; shot q5-scroll
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "LunaSwipe" $OUT/logcat-all.txt > $OUT/swipe-trace.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | grep -v "RuntimeInit\|Monkey\|boot image\|lock profiling\|VM exiting" | head -50 > $OUT/logcat-errors.txt
ls -la $OUT
