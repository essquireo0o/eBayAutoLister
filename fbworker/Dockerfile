# fb-worker — headless Facebook Marketplace runner for the HOSTED build. See fbworker/server.mjs
# for what it is and, more importantly, why it must never be published to the host.
#
# Built ON THE SERVER (the base image is pulled straight from Microsoft's registry over the box's
# own fast network), NOT shipped over the workstation uplink like the app image. See HOSTING.md.
#
#   docker build -f fbworker/Dockerfile -t ing-fb-worker:latest .
#
# The Playwright version here MUST match the Chromium baked into the base tag — that is the whole
# point of using the official image. Bump the tag and the `npm i` version together, never one alone.
FROM mcr.microsoft.com/playwright:v1.55.0-jammy

# Playwright installed at a FIXED, known path. The app substitutes this exact path into %%PW%% when
# it generates the search script (NodeRuntime.PlaywrightDir on the hosted build), so the script's
# require('<abs>/playwright') resolves here. Browsers themselves are already in the image at
# /ms-playwright (PLAYWRIGHT_BROWSERS_PATH), so this pulls the JS package only, not the browsers.
WORKDIR /pw
RUN npm i playwright@1.55.0 --no-audit --no-fund --loglevel=error

COPY fbworker/server.mjs /app/server.mjs

ENV PORT=8090
# Non-root: pwuser is created by the base image and owns the browser cache.
USER pwuser
EXPOSE 8090
CMD ["node", "/app/server.mjs"]
