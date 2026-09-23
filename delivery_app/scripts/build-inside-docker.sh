#!/bin/bash
export WHITELABEL_APP_NAME="$APP_NAME"
flutter pub get
flutter build apk --release --android-skip-build-dependency-validation --dart-define="API_BASE_URL=$API_BASE_URL" --dart-define="APP_NAME=$APP_NAME"
