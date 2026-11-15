---
name: release
description: Wydaje nową wersję Informix.Data i publikuje ją na GitHubie (tag vX.Y.Z → workflow Release → GitHub Packages + GitHub Release). Użyj, gdy użytkownik mówi "wydaj nową wersję", "zrób release", "opublikuj wersję", "wydaj major/minor". Argument: major | minor | patch (domyślnie minor).
argument-hint: "[minor|major|patch]"
---

# Wydanie nowej wersji Informix.Data

Wersja paczki pochodzi wyłącznie z tagu git (MinVer, prefiks `v`). Wydanie = utworzenie
annotowanego tagu `vX.Y.Z` na aktualnym `main` i wypchnięcie go. Resztę (testy, pack,
push do GitHub Packages, GitHub Release) robi `.github/workflows/release.yml`.

Wywołanie skilla przez użytkownika jest zgodą na utworzenie i wypchnięcie tagu — nie pytaj
ponownie, chyba że któryś warunek poniżej nie jest spełniony.

## 1. Rodzaj podbicia

Z argumentu / wypowiedzi użytkownika:
- `major` → `X+1.0.0`
- `minor` → `X.Y+1.0` (**domyślne**, gdy użytkownik nic nie powiedział)
- `patch` → `X.Y.Z+1` (tylko gdy użytkownik wprost o to poprosi)

## 2. Warunki wstępne (przerwij i zgłoś, jeśli któryś nie jest spełniony)

```bash
git fetch origin --tags
git rev-parse --abbrev-ref HEAD                       # musi być: main
git status --porcelain --untracked-files=no           # musi być puste (pliki nieśledzone nie przeszkadzają)
git rev-parse HEAD origin/main                        # oba SHA muszą być identyczne
```

- Jeśli lokalny `main` ma commity niewypchnięte → zapytaj użytkownika, czy wypchnąć `main`
  (nie rób tego samodzielnie).
- Jeśli `origin/main` jest przed lokalnym → zgłoś, nie taguj.
- Plik `.github/workflows/release.yml` musi istnieć w commicie, który tagujesz.

Uruchom testy lokalnie (trwa kilka sekund, nie wymagają serwera Informix):

```bash
dotnet test Informix.Data.Tests -c Release
```

Jeśli którykolwiek test nie przechodzi → przerwij i pokaż błędy.

## 3. Wyznacz nową wersję

```bash
git tag --list "v*" --sort=-v:refname
```

- Weź najwyższy tag **bez** sufiksu pre-release (np. pomiń `v1.2.0-beta.1`).
- Brak jakiegokolwiek tagu → pierwsze wydanie to **`v1.0.0`** niezależnie od rodzaju podbicia.
- Upewnij się, że wyliczony tag jeszcze nie istnieje (lokalnie i na `origin`).
- Jeśli od ostatniego tagu nie ma żadnych commitów (`git rev-list <ostatni-tag>..HEAD --count` == 0)
  → zgłoś, że nie ma czego wydawać, i przerwij.

Pokaż użytkownikowi krótko: poprzednia wersja → nowa wersja oraz listę commitów od
ostatniego tagu (`git log <ostatni-tag>..HEAD --oneline`, przy pierwszym wydaniu ostatnie ~15).

## 4. Utwórz i wypchnij tag

```bash
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```

Wypychaj **tylko tag** — nie `main`, nie inne tagi (nigdy `git push --tags`).

## 5. Sprawdź wynik

Opcjonalnie sprawdź lokalnie, że MinVer widzi nową wersję:

```bash
dotnet pack Informix.Data/Informix.Data.csproj -c Release -o "$SCRATCHPAD/nupkg-check"
```

(plik powinien nazywać się `Informix.Data.X.Y.Z.nupkg`; użyj katalogu scratchpad, nie repo).

Workflow uruchamia się na GitHubie. Jeśli dostępne jest `gh`, śledź go:

```bash
gh run list --workflow release.yml --limit 1
gh run watch <id> --exit-status
```

W przeciwnym razie podaj użytkownikowi linki:
- https://github.com/robsosno/Informix.Data/actions/workflows/release.yml
- https://github.com/robsosno/Informix.Data/releases/tag/vX.Y.Z

## Gdy workflow się nie powiedzie

Tag już jest na GitHubie, ale paczka mogła nie zostać opublikowana. Nie usuwaj tagu
samodzielnie — opisz błąd i zaproponuj: poprawkę + nowy tag patch, albo (za zgodą
użytkownika) usunięcie tagu (`git push origin :refs/tags/vX.Y.Z` i `git tag -d vX.Y.Z`)
i ponowne wydanie. Wersji już opublikowanej w GitHub Packages nie da się nadpisać.
