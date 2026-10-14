#!/bin/bash
# "Opening the app stops the music": play, then reopen the app three ways (after Home, after
# Back, and over the background player) and record whether audio is still playing.
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
launch() { adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1; sleep 60; anr; sleep 20; anr; }
state() {
  echo "== $1" >> $OUT/state.txt
  adb shell dumpsys media_session | grep -A3 "package=$PKG" | grep -E "state=PlaybackState" >> $OUT/state.txt
  echo "pid: $(adb shell pidof $PKG)" >> $OUT/state.txt
  adb shell dumpsys audio | grep -E "u/pid:$AUID" | sed 's/^ *//' >> $OUT/state.txt
  adb shell dumpsys audio | grep -A3 "Audio Focus stack entries" | grep -o "pack: [^ ]* -- client: [^ ]*" >> $OUT/state.txt
  echo "stream requests so far: $(grep -c 'stream' mock.log)" >> $OUT/state.txt
}
APK=$(ls publish/android/*-Signed.apk | head -1)
adb shell settings put global hide_error_dialogs 1
sleep 60
adb install -r "$APK"
AUID=$(adb shell dumpsys package $PKG | grep -m1 -o "userId=[0-9]*" | cut -d= -f2)
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
for ime in $(adb shell ime list -s); do adb shell ime disable "$ime"; done
adb logcat -c
launch; shot 01-launch
tap 540 664; sleep 2; adb shell input keycombination 113 29; adb shell input text "http://10.0.2.2:4533"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
adb shell input keyevent 61; sleep 1; adb shell input text "test"
tap 540 1260
sleep 25; anr; shot 02-home
tap 811 1734; sleep 15; anr
tap 60 127; sleep 3; anr; tap 113 252; sleep 15; anr; shot 03-playing
state "1 playing"
adb shell input keyevent 3; sleep 10                 # Home
state "2 after Home"
launch; shot 04-reopened-after-home
state "3 reopened after Home"
adb shell input keyevent 4; sleep 3; adb shell input keyevent 4; sleep 10   # Back (twice: a page, then the app)
state "4 after Back"
shot 05-after-back
launch; shot 06-reopened-after-back
state "5 reopened after Back"
adb shell input keyevent 3; sleep 3
adb shell am kill $PKG; sleep 3; adb shell am force-stop $PKG; sleep 3
adb shell cmd media_session dispatch play; sleep 25
state "6 background player (no app)"
launch; shot 07-opened-over-background-player
state "7 opened over the background player"
sleep 20
state "8 20 s later"
adb logcat -d -v time > $OUT/logcat-all.txt
grep -E "AndroidRuntime|FATAL|Exception" $OUT/logcat-all.txt | head -50 > $OUT/logcat-errors.txt
grep -E "LunaDrome\]|DOTNET" $OUT/logcat-all.txt | head -300 > $OUT/logcat-luna.txt
adb shell run-as $PKG sh -c "cat files/.config/LunaDrome-Uno/playback.log 2>/dev/null; cat files/LunaDrome-Uno/playback.log 2>/dev/null; find . -name playback.log" > $OUT/playback.log 2>&1
cat $OUT/state.txt
ls -la $OUT
