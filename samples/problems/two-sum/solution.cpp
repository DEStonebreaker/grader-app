// Reference solution. Compile on the judge as the judge user, into a directory
// it owns — /tmp is not writable for it under the host's hardening:
//   sudo -u judge g++ -O2 -std=c++20 -o /var/lib/grader/build/two-sum solution.cpp
#include <cstdio>
#include <vector>

int main() {
    int n, t;
    if (scanf("%d %d", &n, &t) != 2) return 1;

    std::vector<int> a(n);
    for (int i = 0; i < n; ++i)
        if (scanf("%d", &a[i]) != 1) return 1;

    for (int i = 0; i < n; ++i)
        for (int j = i + 1; j < n; ++j)
            if (a[i] + a[j] == t) {
                printf("%d %d\n", i, j);
                return 0;
            }

    return 1;
}