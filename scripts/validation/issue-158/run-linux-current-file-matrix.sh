#!/usr/bin/env bash
set -euo pipefail

candidate=''
source_repo=''
run_id=''
runtime_marker=''
sdk_root=''
top=''
owner=''
expected_owner=''
owned=0
captured=''

capture() {
  captured="$("$@" 2>/dev/null)" || return 1
}

is_decimal() {
  [[ "$1" =~ ^(0|[1-9][0-9]{0,9})$ ]] || return 1
}

cleanup() {
  [[ "${owned}" == 1 ]] || return 1
  [[ -d "${top}" && ! -L "${top}" ]] || return 1
  capture realpath -e -- "${top}" || return 1
  [[ "${captured}" == "${top}" ]] || return 1
  [[ -f "${owner}" && ! -L "${owner}" ]] || return 1
  capture realpath -e -- "${owner}" || return 1
  [[ "${captured}" == "${owner}" ]] || return 1
  capture stat -c %h -- "${owner}" || return 1
  [[ "${captured}" == 1 ]] || return 1
  capture stat -c %s -- "${owner}" || return 1
  is_decimal "${captured}" || return 1
  [[ "${captured}" -eq "${#expected_owner}" ]] || return 1
  capture cat -- "${owner}" || return 1
  [[ "${captured}" == "${expected_owner}" ]] || return 1
  capture findmnt -rn -o TARGET || return 1
  local mount_target
  while IFS= read -r mount_target; do
    [[ "${mount_target}" != "${top}" && "${mount_target}" != "${top}/"* ]] || return 1
  done <<< "${captured}"
  local entry name links entry_real
  local -a entries=()
  coproc CAO_ISSUE158_FIND { find "${top}" -xdev -mindepth 1 -maxdepth 1 -print0 2>/dev/null; } || return 1
  local find_pid="${CAO_ISSUE158_FIND_PID-}"
  local find_fd="${CAO_ISSUE158_FIND[0]-}"
  [[ "${find_pid}" =~ ^[0-9]+$ && "${find_fd}" =~ ^[0-9]+$ ]] || return 1
  while IFS= read -r -d '' entry <&"${find_fd}"; do entries+=("${entry}"); done
  wait "${find_pid}" || return 1
  exec {find_fd}<&- || return 1
  for entry in "${entries[@]}"; do
    name="${entry##*/}"
    case "${name}" in
      .cao-issue-158-linux-owner.json|prepared.json|test-output.txt)
        [[ -f "${entry}" && ! -L "${entry}" ]] || return 1
        capture stat -c %h -- "${entry}" || return 1
        links="${captured}"
        [[ "${links}" == 1 ]] || return 1
        ;;
      checkout|work)
        [[ -d "${entry}" && ! -L "${entry}" ]] || return 1
        ;;
      *) return 1 ;;
    esac
    capture realpath -e -- "${entry}" || return 1
    entry_real="${captured}"
    [[ "${entry_real}" == "${entry}" ]] || return 1
  done
  rm -rf --one-file-system -- "${top}" 2>/dev/null || return 1
  [[ ! -e "${top}" && ! -L "${top}" ]] || return 1
}

blocked() {
  local code="$1"
  if [[ "${owned}" == 1 ]]; then
    cleanup || return 1
    owned=0
  fi
  [[ ! -e "${top}" && ! -L "${top}" ]] || return 1
  printf 'issue158_linux_blocked=%s\n' "${code}" || return 1
  exit 1
}

validate_source() {
  [[ "${source_repo}" =~ ^/mnt/[a-z]/.+$ && -d "${source_repo}" && ! -L "${source_repo}" ]] || return 1
  capture realpath -e -- "${source_repo}" || return 1
  [[ "${captured}" == "${source_repo}" ]] || return 1
}

validate_sdk() {
  [[ "${sdk_root}" == /tmp/cao-issue158-dotnet-sdk-10.0.203 && -d "${sdk_root}" && ! -L "${sdk_root}" ]] || return 1
  capture realpath -e -- "${sdk_root}" || return 1
  [[ "${captured}" == "${sdk_root}" ]] || return 1
  dotnet_binary="${sdk_root}/dotnet"
  [[ -f "${dotnet_binary}" && ! -L "${dotnet_binary}" && -x "${dotnet_binary}" ]] || return 1
  capture realpath -e -- "${dotnet_binary}" || return 1
  [[ "${captured}" == "${dotnet_binary}" ]] || return 1
  capture "${dotnet_binary}" --version || return 1
  [[ "${captured}" == 10.0.203 ]] || return 1
}

preflight_tools() {
  local tool
  for tool in git realpath cat find basename rm mkdir stat base64 timeout grep findmnt; do
    command -v "${tool}" >/dev/null 2>&1 || return 1
  done
}

run_main() {
  exec 2>/dev/null
  candidate="${1-}"
  source_repo="${2-}"
  run_id="${3-}"
  runtime_marker="${4-}"
  sdk_root="${5-}"
  top="/tmp/cao-issue158-linux-${run_id}"
  owner="${top}/.cao-issue-158-linux-owner.json"
  expected_owner="{\"schema_version\":\"issue-158-linux-owner.v1\",\"run_id\":\"${run_id}\",\"candidate_sha\":\"${candidate}\"}"
  owned=0
  trap 'if [[ "${owned}" == 1 ]]; then cleanup >/dev/null 2>&1 || true; fi' EXIT
  [[ "${candidate}" =~ ^[0-9a-f]{40}$ && "${run_id}" =~ ^[0-9a-f]{32}$ && -n "${runtime_marker}" ]] || blocked argument
  preflight_tools || blocked prerequisite_tool
  validate_source || blocked source
  [[ ! -e "${top}" && ! -L "${top}" ]] || exit 1
  validate_sdk || blocked prerequisite_dotnet
  mkdir -- "${top}" 2>/dev/null || blocked top
  printf '%s' "${expected_owner}" > "${owner}" || exit 1
  owned=1
  [[ ! -L "${top}" ]] || exit 1
  capture realpath -e -- "${top}" || exit 1
  [[ "${captured}" == "${top}" ]] || exit 1
  checkout="${top}/checkout"
  git clone --quiet --no-local --no-hardlinks -- "${source_repo}" "${checkout}" >/dev/null 2>&1 || blocked clone
  git -C "${checkout}" checkout --quiet --detach "${candidate}" >/dev/null 2>&1 || blocked checkout
  capture git -C "${checkout}" rev-parse HEAD || blocked checkout
  [[ "${captured}" == "${candidate}" ]] || blocked checkout
  capture git -C "${checkout}" status --porcelain=v1 --untracked-files=normal || blocked checkout
  [[ -z "${captured}" ]] || blocked checkout
  case "${checkout}" in /mnt/[a-z]/*) blocked filesystem ;; esac
  capture findmnt -T "${checkout}" -n -o FSTYPE || blocked filesystem
  [[ "${captured}" == ext4 ]] || blocked filesystem
  work="${top}/work"
  mkdir -- "${work}" 2>/dev/null || blocked filesystem
  case "${work}" in /mnt/[a-z]/*) blocked filesystem ;; esac
  capture findmnt -T "${work}" -n -o FSTYPE || blocked filesystem
  [[ "${captured}" == ext4 ]] || blocked filesystem
  result="${top}/prepared.json"
  export CAO_ISSUE158_LINUX_AUTHORIZED='issue-158-linux-ext4-current-file-v1' CAO_ISSUE158_CANDIDATE_SHA="${candidate}" CAO_ISSUE158_RUN_ID="${run_id}" CAO_ISSUE158_LINUX_REPOSITORY="${checkout}" CAO_ISSUE158_LINUX_WORK_ROOT="${work}" CAO_ISSUE158_LINUX_RESULT_FILE="${result}"
  test_output="${top}/test-output.txt"
  timeout --signal=TERM --kill-after=30s 10m "${dotnet_binary}" test "${checkout}/tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj" --filter 'Issue158Lane=LinuxExt4CurrentFile' --logger 'console;verbosity=minimal' >"${test_output}" 2>&1 || blocked test
  capture stat -c %s -- "${test_output}" || blocked test
  is_decimal "${captured}" || blocked test
  [[ "${captured}" -le 1048576 ]] || blocked test
  grep -Eq 'Passed|合格' "${test_output}" 2>/dev/null || blocked test
  rm -- "${test_output}" 2>/dev/null || blocked cleanup
  [[ -f "${result}" && ! -L "${result}" ]] || blocked result
  capture stat -c %s -- "${result}" || blocked result
  is_decimal "${captured}" || blocked result
  [[ "${captured}" -gt 0 && "${captured}" -le 32768 ]] || blocked result
  capture base64 -w0 -- "${result}" || blocked result
  encoded="${captured}"
  cleanup || exit 1
  owned=0
  printf 'issue158_linux_prepared=%s\n' "${encoded}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  run_main "$@"
fi
