#!/bin/sh
set -eu

envsubst '${VITE_ENTRA_CLIENT_ID} ${VITE_ENTRA_TENANT_ID} ${VITE_TMS_API_URL} ${VITE_TMS_API_SCOPE}' \
  < /usr/share/nginx/html/config.js.template \
  > /usr/share/nginx/html/config.js
