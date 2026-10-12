# 브랜치와 병합 규칙

이 레포에서 브랜치를 만들고 main에 합치는 방법을 정리합니다. GitHub에 걸어 둔 보호 규칙도 함께 적습니다.

## 브랜치 이름

main에서 새 브랜치를 만들고, 이름 앞에 작업 종류를 붙입니다.

| 접두어 | 쓰는 경우 |
|---|---|
| `feat/` | 새 기능 |
| `fix/` | 버그 수정 |
| `refactor/` | 동작은 그대로 두고 코드만 정리 |
| `docs/` | 문서, 스크린샷 |
| `ci/` | GitHub Actions 워크플로 |
| `chore/` | 그 밖의 레포 관리 |

## main에 합치는 방법

지금은 초기 단계라 PR을 거치지 않습니다. 작업 브랜치를 로컬에서 squash 병합한 뒤 main에 바로 push합니다.

```bash
git checkout main
git pull --ff-only
git merge --squash <작업 브랜치>
git commit            # 제목은 아래 "버전이 정해지는 방식"에 맞춥니다
git push origin main
```

앱이 안정되었다고 판단하면 PR로 합치는 방식으로 바꿉니다. 그때 main 보호 규칙에 "PR 필수"를 추가합니다.

## 버전이 정해지는 방식

main에 push하면 Release 워크플로(`.github/workflows/release.yml`)가 새 버전을 만들어 배포합니다. 다만 배포되는 파일이 바뀐 경우에만 돕니다. `src/`, `README.txt`, 워크플로 파일이 그 대상입니다. 문서나 스크린샷만 바꾸면 새 버전이 나오지 않습니다.

버전 번호는 마지막 `vX.Y.Z` 태그 이후 커밋 제목으로 정합니다. 그래서 squash 커밋의 제목을 정할 때 접두어를 신경 써야 합니다.

| 커밋 제목 | 올라가는 자리 | 예 |
|---|---|---|
| `type!:`로 시작하거나 본문에 `BREAKING CHANGE`가 있음 | major | v0.3.1 → v1.0.0 |
| `feat:` | minor | v0.3.1 → v0.4.0 |
| 그 밖의 접두어(`fix:`, `refactor:`, `perf:` 등) | patch | v0.3.1 → v0.3.2 |

## GitHub 보호 설정

| 대상 | 설정 | 이유 |
|---|---|---|
| `main` 브랜치 (ruleset "main 보호") | force push 금지, 삭제 금지 | 실수로 기록이 사라지는 것을 막습니다 |
| `v*` 태그 (ruleset "버전 태그 보호") | 삭제 금지, 다른 커밋으로 옮기기 금지 | Release가 마지막 태그를 기준으로 다음 버전을 계산합니다. 새 태그는 Release가 만들어야 하므로 생성은 막지 않습니다 |
| 병합 방식 | squash만 허용, 병합한 브랜치는 자동 삭제 | 커밋 하나의 제목으로 버전이 정해지게 합니다 |
| 허용 Action | GitHub 공식 Action만 | 외부 Action이 Release 권한을 얻지 못하게 합니다 |
| 취약점 제보 | 비공개 제보 켜짐 | 보안 문제를 공개 이슈가 아니라 Security 탭에서 비공개로 받습니다 |

규칙은 레포 관리자에게도 똑같이 적용됩니다. 정말로 force push나 태그 삭제가 필요하면 Settings > Rules > Rulesets에서 해당 ruleset을 잠시 끄고 작업한 뒤 다시 켭니다.
