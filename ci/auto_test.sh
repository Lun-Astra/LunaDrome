#!/bin/bash
# Android Auto without the app: after a normal login + play, the app process is killed, then
# (1) cover art is read through the content provider and (2) a media "play" button starts the
# background player, with no activity. Then the app is opened and takes the queue over.
set -x
OUT=ui-test-out; mkdir -p $OUT
PKG=com.lunastra.lunadrome
shot() { adb exec-out screencap -p > "$OUT/$1.png"; }
anr() {
  for i in 1 2 3; do
    if adb shell dumpsys window | grep -qi "Application Not Responding"; then
      adb shell input tap 300 1084; sleep 5
    else return; fi
  done
}
tap() { anr; adb shell input tap "$@"; }
state() { echo "== $1" >> $OUT/state.txt; adb shell dumpsys media_session | grep -E "package=|state=PlaybackState|active=" >> $OUT/state.txt;
          echo "pid: $(adb shell pidof $PKG)" >> $OUT/state.txt;
          adb shell dumpsys activity activities | grep -E "ResumedActivity|$PKG/" | head -5 >> $OUT/state.txt;
          adb shell dumpsys audio | grep -A4 "Audio Focus stack entries" >> $OUT/state.txt; }
APK=$(ls publish/android/*-Signed.apk | head -1)
adb shell settings put global hide_error_dialogs 1
sleep 60
adb install -r "$APK"
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
for ime in $(adb shell ime list -s); do adb shell ime disable "$ime"; done
adb logcat -c
adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1
sleep 60; anr; sleep 20; anr; shot 01-launch
tap 540 664; sleep 2; adb shell input keycombination 113 29; adb shell input text "http://10.0.2.2:4533"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
tap 540 1260
sleep 25; anr; shot 02-home
tap 811 1734; sleep 15; anr
tap 60 127; sleep 3; anr; tap 113 252; sleep 10; anr; shot 03-playing
state "app playing"
adb shell cmd media_session dispatch pause; sleep 5
state "app paused"
adb shell input keyevent 3; sleep 5          # home
adb shell am kill $PKG; sleep 5
state "killed"
echo "MARK-ART" >> $OUT/marks.txt; date >> $OUT/marks.txt
adb shell content read --uri content://$PKG.art/al1 > $OUT/art.bin; echo "art exit=$?"
file $OUT/art.bin; xxd $OUT/art.bin | head -2
state "after art"
adb shell am kill $PKG; sleep 3
echo "MARK-PLAY" >> $OUT/marks.txt; date >> $OUT/marks.txt
adb shell cmd media_session dispatch play; sleep 20
state "after play button (no app)"
shot 04-no-app
adb shell cmd media_session dispatch next; sleep 10
state "after next (no app)"
echo "MARK-OPEN" >> $OUT/marks.txt; date >> $OUT/marks.txt
adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1
sleep 60; anr; sleep 20; anr; shot 05-app-opened
state "app opened"
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | head -50 > $OUT/logcat-errors.txt
grep -E "LunaDrome\]|DOTNET" $OUT/logcat-all.txt | head -200 > $OUT/logcat-luna.txt
cat $OUT/state.txt
ls -la $OUT
