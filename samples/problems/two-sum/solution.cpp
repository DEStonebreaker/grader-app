// Reference solution. Compile on the judge with:
//   g++ -O2 -std=c++20 -o /tmp/two-sum solution.cpp
#include <cstdio>
#include <vector>

int main() {
    int n, t;
    if (scanf("%d %d", &n, &t) != 2) return 1;

    std::vector<int> a(n);
    for (int i = 0; i < n; ++i) scanf("%d", &a[i]);

    for (int i = 0; i < n; ++i)
        for (int j = i + 1; j < n; ++j)
            if (a[i] + a[j] == t) {
                printf("%d %d\n", i, j);
                return 0;
            }

    return 1;
}