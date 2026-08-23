#!/usr/bin/env bash
set -euo pipefail
candidate="${1-}"
source_repo="${2-}"
run_id="${3-}"
runtime_marker="${4-}"
top="/tmp/cao-issue158-linux-${run_id}"
owner="${top}/.cao-issue-158-linux-owner.json"
expected_owner="{\"schema_version\":\"issue-158-linux-owner.v1\",\"run_id\":\"${run_id}\",\"candidate_sha\":\"${candidate}\"}"
owned=0
cleanup() {
  if [[ "${owned}" == 1 && -d "${top}" && ! -L "${top}" && "$(realpath -e -- "${top}")" == "${top}" && "$(cat -- "${owner}" 2>/dev/null || true)" == "${expected_owner}" ]]; then
    while IFS= read -r -d '' entry; do
      case "$(basename -- "${entry}")" in
        .cao-issue-158-linux-owner.json|checkout|work|prepared.json|test-output.txt) ;;
        *) return 1 ;;
      esac
      [[ ! -L "${entry}" ]]
    done < <(find "${top}" -mindepth 1 -maxdepth 1 -print0)
    rm -rf --one-file-system -- "${top}"
  fi
}
trap cleanup EXIT
[[ "${candidate}" =~ ^[0-9a-f]{40}$ && "${run_id}" =~ ^[0-9a-f]{32}$ && -n "${runtime_marker}" ]]
[[ "${source_repo}" == /mnt/c/* && ! -e "${top}" ]]
mkdir -- "${top}"
printf '%s' "${expected_owner}" > "${owner}"
owned=1
[[ ! -L "${top}" && "$(realpath -e -- "${top}")" == "${top}" ]]
checkout="${top}/checkout"
git clone --quiet --no-local --no-hardlinks -- "${source_repo}" "${checkout}"
git -C "${checkout}" checkout --quiet --detach "${candidate}"
[[ "$(git -C "${checkout}" rev-parse HEAD)" == "${candidate}" ]]
[[ -z "$(git -C "${checkout}" status --porcelain=v1 --untracked-files=normal)" ]]
[[ "${checkout}" != /mnt/c/* && "$(findmnt -T "${checkout}" -n -o FSTYPE)" == ext4 ]]
work="${top}/work"
mkdir -- "${work}"
[[ "$(findmnt -T "${work}" -n -o FSTYPE)" == ext4 ]]
result="${top}/prepared.json"
export CAO_ISSUE158_LINUX_AUTHORIZED='issue-158-linux-ext4-current-file-v1'
export CAO_ISSUE158_CANDIDATE_SHA="${candidate}"
export CAO_ISSUE158_RUN_ID="${run_id}"
export CAO_ISSUE158_LINUX_REPOSITORY="${checkout}"
export CAO_ISSUE158_LINUX_WORK_ROOT="${work}"
export CAO_ISSUE158_LINUX_RESULT_FILE="${result}"
test_output="${top}/test-output.txt"
timeout --signal=TERM 10m dotnet test "${checkout}/tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj" --filter 'Issue158Lane=LinuxExt4CurrentFile' --logger 'console;verbosity=minimal' >"${test_output}" 2>&1
[[ "$(stat -c %s -- "${test_output}")" -le 1048576 ]]
grep -Eq 'Passed|合格' "${test_output}"
rm -- "${test_output}"
[[ -f "${result}" && ! -L "${result}" ]]
size="$(stat -c %s -- "${result}")"
[[ "${size}" -gt 0 && "${size}" -le 32768 ]]
encoded="$(base64 -w0 -- "${result}")"
cleanup
owned=0
[[ ! -e "${top}" ]]
printf 'issue158_linux_prepared=%s\n' "${encoded}"
