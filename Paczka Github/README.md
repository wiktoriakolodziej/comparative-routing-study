# Uruchomienie pomiarów

Potrzebujesz Windows i .NET 8 SDK. Zostaw ten folder wewnątrz repozytorium — skrypty budują i uruchamiają kod projektu. Przy pierwszym uruchomieniu potrzebny jest internet do pobrania zależności.

Jeśli pobierasz repozytorium przez Git, zainstaluj Git LFS i wykonaj `git lfs pull`, żeby pobrać właściwe pliki z katalogu `Data`.

- **Benchmark.cmd** — porównanie Dijkstry, CSA i RAPTOR-a. Podaj liczbę zapytań, promień przejść pieszych i limit przesiadek, a potem wybierz ziarno, algorytmy i zbiory danych. Strzałkami zmieniasz pozycję, spacją zaznaczasz, Enter zatwierdza. Wyniki trafiają do `Reports`.
- **RAPTOR-Realtime.cmd** — badanie RAPTOR-a z aktualizacjami GZM. Tryb online wymaga internetu i sam pobiera rozkład na bieżący dzień. Tryb offline odtwarza zapisaną aktualizację; potrzebujesz do niego własnego pliku `.pb` i zgodnego rozkładu GTFS. Raporty i pobrane dane trafiają do `DataRealTime/Slask`.

W menu realtime można wybrać określoną liczbę pomiarów albo pracę do zatrzymania skrótem Ctrl+C. Po zakończeniu zachowaj cały katalog wyników danej sesji.
